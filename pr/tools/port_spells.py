"""Port BNW spells/skills (SNES slots 0-255) and espers onto PR abilities.

Slot-based: PR ability idmap.spell(i) becomes BNW's spell i, because every BNW
reference (espers, weapon procs, AI, Lores) uses BNW slot numbers. Presentation
(animation, menu category, icon) comes from a *template*: the vanilla spell with
the same identity as BNW's spell (by name / alias), so e.g. BNW slot 24 "Drain"
looks and sorts like vanilla Drain even though it sits in vanilla Scan's slot.
"""
import os
import re
from collections import defaultdict

import bnw_text
import ff6snes as S
import idmap as M
import learn
import prdata as P

# SNES status bit -> PR condition id (learned from vanilla single-status spells)
STATUS_TO_COND = {0: 9, 1: 15, 2: 10, 4: 33, 5: 13, 6: 11, 7: 5, 8: 14, 9: 4, 10: 19, 11: 6,
                  12: 18, 13: 12, 14: 16, 15: 7, 17: 20, 18: 25, 19: 21, 20: 26, 21: 22,
                  22: 23, 23: 24, 25: 32, 26: 31, 29: 28, 31: 17}
# Removal groups also list each status's "permanent" twin.
COND_TWIN = {10: 88, 9: 89, 16: 90, 33: 87, 17: 96, 23: 93, 22: 94, 24: 92, 21: 95, 18: 91, 5: 100}
STATUS_FUNCS = {9, 10, 12}
LIFT_FLAG = 0x04  # flags3 bit: spell removes its statuses instead of adding them

# BNW name -> vanilla SNES name of the spell whose PR look to borrow.
ALIASES = {
    "holy": "pearl", "rerise": "life 3", "restore": "remedy", "hastex": "haste2",
    "slowx": "slow 2", "sleepx": "sleep", "regenx": "regen", "sap": "poison",
    "dark": "bio", "storm": "aero", "muddle": "muddle", "ninja fire": "fire skean",
    "ninja wave": "water edge", "ninja bolt": "bolt edge", "razor leaf": "rage",
    "moonlight": "specter", "bedevil": "specter", "avalanche": "land slide",
    "mirage": "sonic boom", "meerkat": "kitty", "jackrabbit": "whump", "raccoon": "wild bear",
    "toxic frog": "pois. frog", "snowbird": "ice rabbit", "trifecta": "chocobop",
    "blackjack": "h-bomb", "solitaire": "7-flush", "aqualung": "aqua rake",
    "bad breath": "sour mouth", "black omen": "l.4 flare", "blow fish": "blow fish",
    "discord": "dischord", "holy wind": "pearl wind", "leech": "raid", "raze": "fire wall",
    "refract": "reflect???", "safeguard": "big guard", "tsunami": "cleansweep",
    "head bonk": "megahit", "boil": "fire wall", "starlight": "pearl wind", "repair": "pep up",
    "fireball": "fire ball", "volt array": "diffuser", "discharge": "wavecannon",
    "magnitude": "magnitude8", "barrier": "tekbarrier", "g-force": "50 gs", "n.cross": "n. cross",
    "grab": "seize", "polarity": "r.polarity", "s.cross": "s. cross", "hyperdrive": "hyperdrive",
    "pale omen": "dread", "thriller": "disaster", "condemn": "condemned", "soul eater": "soul out",
    "purge": "revenger", "glare": "dread", "aero": "aero", "step mine": "step mine",
    "air blast": "aero", "blight": "scar beam", "snot rocket": "sneeze", "brown note": "lullaby",
    "landslide": "slide", "riot blast": "riot blade", "dark heart": "back blade",
    "ninja flip": "shadowfang", "last ditch": "royalshock", "hadouken": "tigerbreak",
    "tornado": "spin edge", "soul reaver": "sabresoul", "full house": "red card",
    "mig rush": "moogle rush", "reprisal": "takedown", "good boy": "wild fang",
    "go fish": "lagomorph", "loki": "raiden", "seraph": "sraphim", "zoneseek": "zoneseek",
    "aqua slash": "aqua rake", "meltdown": "merton", "exploder": "exploder", "rock": "land slide",
    "tentacle": "tentacle", "shrapnel": "shrapnel", "blink": "clear", "defib": "life",
}


def norm(name):
    return re.sub(r"[^a-z0-9.?]", " ", name.lower()).strip()


def fsig(fg_table, gid):
    if gid not in fg_table.rows:
        return []
    r = fg_table[gid]
    out = []
    for k in (1, 2, 3):
        f = int(r[f"function{k}_id"])
        if f:
            out.append([f, int(r[f"function{k}_value1"]), int(r[f"function{k}_value2"]), int(r[f"function{k}_value3"])])
    return out


def status_bits(spell):
    s = spell.status
    v = s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24
    return [b for b in range(32) if v >> b & 1]


def spell_record(s):
    return {"targets": s.targets, "elements": s.elements, "flags1": s.flags1, "flags2": s.flags2,
            "flags3": s.flags3, "mp": s.mp, "power": s.power, "flags4": s.flags4,
            "accuracy": s.accuracy, "effect": s.effect, "status": s.status}


class SpellPorter:
    def __init__(self, van, bnw, tables, messages, log, profile=None):
        import profiles
        self.profile = profile or profiles.PROFILES["bnw"]
        self.van, self.bnw, self.log = van, bnw, log
        self.ab = tables.setdefault("ability", P.Table("ability"))
        self.vab = P.Table("ability")  # untouched vanilla rows: templates must never see ported data
        self.fg = tables.setdefault("function_group", P.Table("function_group"))
        self.cg = tables.setdefault("condition_group", P.Table("condition_group"))
        self.le = tables.setdefault("learning", P.Table("learning"))
        self.messages = messages
        self.content = P.Table("content")
        self.msg = P.messages()
        self.vsp = S.spells(van)
        self.bsp = S.spells(bnw)
        self.changed = defaultdict(set)
        self.next_group = max(int(r["group_id"]) for r in self.cg) + 1
        self.next_cg_row = max(self.cg.rows) + 1
        self.next_fg = max(self.fg.rows) + 1
        self.physical_swaps = 0
        self.fg_cache = {}
        pairs = [(spell_record(s), self.vab[M.spell(s.id)]) for s in self.vsp if M.spell(s.id) in self.vab.rows]
        self.maps = learn.learn(pairs, list(pairs[0][0]), ["accuracy_rate", "attribute_id",
                                "attribute_group_id", "renge_id", "battle_renge_id", "menu_renge_id"],
                                min_purity=0.85)
        self.content = tables.setdefault("content", self.content)
        self.content_by_ability = {int(r["type_value"]): r for r in self.content if r["type_id"] == "4"}
        self._index_names()

    # --- templates ------------------------------------------------------------
    def _index_names(self):
        self.by_name = {}
        for s in self.vsp:
            if s.name and M.spell(s.id) in self.vab.rows:  # only spells the PR actually has
                self.by_name.setdefault(norm(s.name), s.id)
        inv = {}
        for s in self.vsp:
            k = M.spell(s.id)
            if k is not None:
                inv[k] = s.id
        for k, row in self.content_by_ability.items():
            if k in inv:
                pr = P.strip_tags(self.msg.get(row["mes_id_name"], ""))
                if pr:
                    self.by_name.setdefault(norm(pr), inv[k])

    def template(self, slot, bnw_name):
        n = norm(bnw_name)
        for key in (n, norm(ALIASES.get(n, ""))):
            if key and key in self.by_name:
                return self.by_name[key], True
        if M.spell(slot) not in self.vab.rows:
            return 179, False  # slot the PR dropped and no name match: borrow a generic enemy skill (Fire Ball)
        return slot, False

    # --- status groups ----------------------------------------------------------
    def new_condition_group(self, conds):
        gid = self.next_group
        self.next_group += 1
        for c in conds:
            rid = self.next_cg_row
            self.next_cg_row += 1
            self.cg.rows[rid] = {"id": str(rid), "group_id": str(gid), "condition_id": str(c),
                                 "type_id": "1", "invocation_rate": "0"}
            self.changed["condition_group"].add(rid)
        return gid

    def function_group(self, funcs):
        key = tuple(tuple(f) for f in funcs)
        if key in self.fg_cache:
            return self.fg_cache[key]
        gid = self.next_fg
        self.next_fg += 1
        row = {"id": str(gid)}
        for k in (1, 2, 3):
            f = funcs[k - 1] if k <= len(funcs) else [0, 0, 0, 0]
            row[f"function{k}_id"], row[f"function{k}_value1"], row[f"function{k}_value2"], row[f"function{k}_value3"] = map(str, f)
        self.fg.rows[gid] = row
        self.changed["function_group"].add(gid)
        self.fg_cache[key] = gid
        return gid

    def physical(self, tmpl_spell, b):
        """SNES spell byte 2 bit 0 "physical": the Fight formula with the spell power as Battle
        Power (the PR's Physical function 2 does that; its Magic function 3 uses Magic and MDef,
        formula probe 2026-11-05). The PR built some physical spells as Magic (vanilla Bum Rush):
        profile.defense_flags "mod" follows the mod's bit everywhere, else only where it changed."""
        want = bool(b.flags1 & 0x01)
        if getattr(self.profile, "defense_flags", "changed") != "mod" and want == bool(tmpl_spell.flags1 & 0x01):
            return None
        return want

    def effects(self, tmpl_spell, tmpl_row, b):
        """Template's function group, with statuses rebuilt if BNW's differ, and the damage
        function made Physical/Magic by the spell's physical bit."""
        gid = int(tmpl_row["battle_function_group_id"])
        tb, bb = status_bits(tmpl_spell), status_bits(b)
        tl, bl = bool(tmpl_spell.flags3 & LIFT_FLAG), bool(b.flags3 & LIFT_FLAG)
        phys = self.physical(tmpl_spell, b)
        sig = fsig(self.fg, gid)
        swap = (phys is True and any(f[0] == 3 for f in sig)) or (phys is False and any(f[0] == 2 for f in sig))
        if tb == bb and tl == bl and not swap:
            return gid
        funcs = [f for f in sig if f[0] not in STATUS_FUNCS]
        if swap:
            funcs = [[2, 0, 0, 0] if phys and f[0] == 3 else [3, 1, 0, 0] if not phys and f[0] == 2 else list(f) for f in funcs]
            self.physical_swaps += 1
        conds = [STATUS_TO_COND[x] for x in bb if x in STATUS_TO_COND]
        if conds:
            if bl:
                conds = conds + [COND_TWIN[c] for c in conds if c in COND_TWIN]
                funcs.append([12, self.new_condition_group(conds), 0, 0])
            elif len(conds) == 1:
                funcs.append([9, conds[0], 0, 0])
            else:
                funcs.append([10, self.new_condition_group(conds), 0, 0])
        return self.function_group(funcs[:3]) if funcs else gid

    # --- main -----------------------------------------------------------------------
    def convert(self, fm, value, fallback):
        out = fm.convert(value) if fm else None
        return out if out is not None else fallback

    def port(self):
        unmatched = []
        for b in self.bsp:
            k = M.spell(b.id)
            if k is None or b.id == 255:
                continue
            if k not in self.ab.rows:
                self.add_missing(k)  # the PR dropped this slot (e.g. 164/170); BNW reuses it
            v = self.vsp[b.id]
            name = b.name or v.name
            tslot, matched = self.template(b.id, name)
            if not matched:
                unmatched.append(f"{b.id}:{name}")
            tmpl_spell = self.vsp[tslot]
            tmpl_row = self.vab[M.spell(tslot)]
            row = self.vab.rows.get(k) or self.ab[k]
            new = dict(tmpl_row)
            new["id"], new["sort_id"] = row["id"], row["sort_id"]
            new["use_value"] = str(b.mp)
            direct_power = tmpl_row["standard_value"] == str(tmpl_spell.power)
            new["standard_value"] = str(b.power) if direct_power else tmpl_row["standard_value"]
            new["accuracy_rate"] = self.convert(self.maps.get("accuracy_rate"), b.accuracy, tmpl_row["accuracy_rate"])
            for col in ("attribute_id", "attribute_group_id"):
                new[col] = self.convert(self.maps.get(col), b.elements, tmpl_row[col])
            for col in ("renge_id", "battle_renge_id", "menu_renge_id"):
                new[col] = self.convert(self.maps.get(col), b.targets, tmpl_row[col])
            new["battle_function_group_id"] = str(self.effects(tmpl_spell, tmpl_row, b))
            if new != row:
                self.ab.rows[k] = new
                self.changed["ability"].add(k)
            renamed = self.profile.rename_all or (b.name or v.name) != v.name
            self.set_text(k, M.spell(tslot), self.display_name(b.id, name) if renamed else None, self.description(b.id))
        self.log(f"physical spells: {self.physical_swaps} damage functions switched Magic <-> Physical by the mod's bit")
        self.log(f"ability: {len(self.changed['ability'])} changed; new function groups "
                 f"{len(self.changed['function_group'])}, new status groups rows {len(self.changed['condition_group'])}")
        self.log(f"  spells using their own slot's look (no name match): {', '.join(unmatched)}")
        return self.changed

    def add_missing(self, k):
        """New ability row + content row + text keys for a PR id that doesn't exist."""
        self.ab.rows[k] = dict(next(iter(self.vab)), id=str(k), sort_id=str(k))
        self.changed["ability"].add(k)
        cid = max(self.content.rows) + 1
        self.content.rows[cid] = {"id": str(cid), "mes_id_name": f"MSG_BNW_MAGIC_NAME_{k}", "mes_id_battle": "None",
                                  "mes_id_description": "None", "icon_id": "0", "type_id": "4",
                                  "type_value": str(k)}
        self.content_by_ability[k] = self.content.rows[cid]
        self.changed["content"].add(cid)

    BUSHIDO_NAMES = 0x0F3C40  # 12 bytes x 8, separate from the skill name table
    LORE_SLOTS = range(139, 151)  # BNW's 12 Lores

    def display_name(self, slot, name):
        if 85 <= slot <= 92:
            return S.decode_name(self.bnw.b(self.BUSHIDO_NAMES + 12 * (slot - 85), 12))
        return name

    def description(self, slot):
        if not hasattr(self, "_descs"):
            self._descs = self.profile.descriptions() or {}
            self._esper_descs = self.profile.esper_descriptions() or []
            self._vdescs = {} if self.profile.rename_all else self.profile.vanilla_descriptions()
        d = self._descs
        if not self.profile.rename_all:  # only text the mod changed
            for key, lo, hi in (("spells", 0, 54), ("lores", 139, 151)):
                if lo <= slot < hi:
                    i = slot - lo
                    new, old = d.get(key, []), self._vdescs.get(key, [])
                    return new[i] if i < len(new) and (i >= len(old) or new[i] != old[i]) else None
            return None
        if not d:
            return None
        if slot < 54:
            return d["spells"][slot]
        if 54 <= slot <= 80:
            return self._esper_descs[slot - 54]
        if 85 <= slot <= 92:
            return d["bushido"][slot - 85]
        if 93 <= slot <= 100:
            return d["blitz"][slot - 93]
        if slot in self.LORE_SLOTS:
            return d["lores"][slot - 139]
        return None

    def set_text(self, k, tmpl_k, name, desc):
        row = self.content_by_ability.get(k)
        tmpl = self.content_by_ability.get(tmpl_k)
        if not row or row["mes_id_name"].startswith("MSG_ITEM_") or row["mes_id_name"] in ("None", ""):
            return  # some skills share their name with an item (scrolls); items own those keys
        icon = ""
        if tmpl:
            m = re.match(r"(<IC_[A-Z]+>)", self.msg.get(tmpl["mes_id_name"], ""))
            icon = m.group(1) if m else ""
        if name:
            self.messages[row["mes_id_name"]] = icon + name
        if desc and row["mes_id_description"] not in ("None", ""):
            self.messages[row["mes_id_description"]] = " ".join(desc.split("\n"))


# --- learning: esper spells, natural magic, Blitz/Bushido levels ------------------
NATURAL = {"terra": (0x2CE3C0, (1, 19)), "celes": (0x2CE3E0, (7,))}
BUSHIDO_LV, BLITZ_LV = 0x26F490, 0x26F498


class LearningPorter:
    def __init__(self, van, bnw, sp, log):
        self.van, self.bnw, self.sp, self.log = van, bnw, sp, log
        self.le = sp.le
        self.ch = sp.changed["learning"]

    def content_for_spell(self, slot):
        return int(self.sp.content_by_ability[M.spell(slot)]["id"])

    def set_row(self, rid, **cols):
        row = self.le[rid]
        new = dict(row)
        for k, v in cols.items():
            new[k] = str(v)
        if new != row:
            self.le.rows[rid] = new
            self.ch.add(rid)

    def neutralize(self, rid):
        # Magicite partials can't delete rows: point leftovers at nothing. A parked row gets
        # learning type 0, which no lookup reads (the game's LearningType: 1 level, 2 magicite,
        # 3 battle background, 4 equipment, 5 job level; each lookup filters by it). Rows parked
        # inside a type matched someone: a level row at job 0 / level 0 taught its spell to every
        # character at the first level check, and a magicite row at value1 0 counted as the
        # spells of "no magicite" (MagicStoneData.GetLearnableAbilityList: type 2 and value1 =
        # the equipped magicite, no job check), so after each battle every character without
        # magicite got those spells' entries (Biggs and Wedge: 19 spells after BNW's first battle,
        # the user's report, reproduced 2026-11-07 with the game harness).
        self.set_row(rid, type_id=0, job_id=0, value1=0, value2=0)

    def new_row(self, template):
        """A new learning row (ids after the table's last), a copy of `template`'s columns."""
        rid = max(self.le.rows) + 1
        row = dict(self.le[template])
        row["id"] = str(rid)
        self.le.rows[rid] = row
        self.ch.add(rid)
        return rid

    @staticmethod
    def natural_learners(rom):
        """Who learns the two natural-magic lists: {"terra": SNES character index or None,
        "celes": ...}. WC moves them in assembly: the routine the JSR at C0/A182 calls compares
        the character (CMP #n) before each list's learner (JSR $A196 = Terra's list, JSR $A1B8 =
        Celes's); NOPs there = nobody. Vanilla/BNW: Terra 0, Celes 6."""
        d = rom.data
        out = {"terra": 0, "celes": 6}
        if d[0x0A182] != 0x20:
            return out
        target = d[0x0A183] | d[0x0A184] << 8          # bank C0
        code = bytes(d[target:target + 64])
        for who, call in (("terra", b"\x20\x96\xA1"), ("celes", b"\x20\xB8\xA1")):
            at = code.find(call)
            if at < 0:
                out[who] = None
                continue
            cmp_at = code.rfind(b"\xC9", 0, at)
            if cmp_at >= 0 and at - cmp_at <= 6:
                out[who] = code[cmp_at + 1]
        return out

    def port(self):
        rows = defaultdict(list)
        for r in self.le:
            rows[(r["type_id"], int(r["job_id"]))].append(int(r["id"]))
        # Esper spells (type 2): one pool of row ids, refilled with BNW's lists.
        pool = sorted(i for (t, j), ids in rows.items() if t == "2" for i in ids)
        entries = []
        bnw_rule = getattr(self.sp.profile, "esper_learning", "snes") == "bnw"
        for e, raw in enumerate(self.bnw.table(*S.ESPERS)):
            esper_content = M.esper_content(e)
            if esper_content is None:
                continue
            for j in ((0, 2, 4) if bnw_rule else (0, 2, 4, 6, 8)):
                first, spell = raw[j], raw[j + 1]
                if spell == 0xff or not first:
                    continue
                if bnw_rule:
                    # BNW: an SP cost; no learning from battles, only the SP bank (rate 0 = no progress)
                    rate = 0 if self.sp.profile.spell_bank_only else max(1, round(100 / first))
                else:
                    rate = first                                  # the SNES learn rate (x1..x20)
                entries.append((esper_content, rate, self.content_for_spell(spell)))
        while len(pool) < len(entries):
            pool.append(self.new_row(pool[0]))                    # WC's espers can teach 5 spells each
        for rid, (esper, rate, content) in zip(pool, entries):
            self.set_row(rid, type_id=2, job_id=0, value1=esper, value2=rate, content_id=content)
        for rid in pool[len(entries):]:
            self.neutralize(rid)
        # Natural magic (type 1, by level), learned by whoever the mod's assembly picks for each list
        sp = S.spells(self.bnw)
        learners = self.natural_learners(self.bnw)
        for who, (off, jobs) in NATURAL.items():
            t = self.bnw.b(off, 32)
            seen, spells = set(), []
            for i in range(0, 32, 2):
                if t[i] != 0xff and t[i] not in seen:
                    seen.add(t[i])
                    spells.append((t[i], t[i + 1]))
            learner = learners[who]
            ids_by_job = {job: sorted(rows[("1", job)]) for job in jobs}
            if learner != (0 if who == "terra" else 6):
                # the list's rows go to the learner's job (PR job = SNES character + 1), or nowhere
                for job in jobs[1:]:
                    for rid in ids_by_job[job]:
                        self.neutralize(rid)
                moved = ids_by_job[jobs[0]]
                if learner is None:
                    for rid in moved:
                        self.neutralize(rid)
                    self.log(f"  {who}'s natural magic: nobody learns it")
                    continue
                ids_by_job = {learner + 1: moved}
                self.log(f"  {who}'s natural magic -> SNES character {learner} (job {learner + 1})")
            if learner == 0:
                spells = [x for x in spells if x[1] > 1]  # PR gives Terra her lv1 spell up front
            for job, ids in ids_by_job.items():
                ids = list(ids)
                while len(ids) < len(spells):
                    ids.append(self.new_row(ids[0]))      # e.g. Terra's 15 after the lv1 one: 14 rows
                for rid, (spell, lv) in zip(ids, spells):
                    self.set_row(rid, job_id=job, value1=lv, content_id=self.content_for_spell(spell))
                for rid in ids[len(spells):]:
                    self.neutralize(rid)
            self.log(f"  {who}: {', '.join(f'{sp[s].name} {lv}' for s, lv in spells)}")
        # Bushido (Cyan, job 3) and Blitz (Sabin, job 6) learn levels.
        for job, off, first in ((3, BUSHIDO_LV, 85), (6, BLITZ_LV, 93)):
            levels = self.bnw.b(off, 8)
            for n, rid in enumerate(sorted(rows[("1", job)])):
                self.set_row(rid, value1=levels[n], content_id=self.content_for_spell(first + n))
        # WC's -sel / -bel (SwdTechs / Blitzes everyone learns): whoever WC gives the command learns
        # them at the same levels too (their own job's rows; WC_FLAGS from tools/wc_seed.py)
        flags = os.environ.get("WC_FLAGS", "").split()
        for flag, snes_cmd, job in (("-sel", 7, 3), ("-bel", 10, 6)):
            if flag not in flags:
                continue
            for ch in S.characters(self.bnw)[:14]:
                if snes_cmd not in ch.commands or ch.id + 1 == job:
                    continue
                for rid in sorted(rows[("1", job)]):
                    new = self.new_row(rid)
                    self.set_row(new, job_id=ch.id + 1)
                self.log(f"  {flag}: character {ch.id} learns job {job}'s techniques by level")
        # Equipment that teaches a spell while worn (type 4: value1 = item content, value2 = rate,
        # content = the spell; SNES item byte 3 = rate, byte 4 = spell): the mod's (WC drops some:
        # -pd's Life spells), rows that lost theirs never teach (rate 0)
        item_rows = {int(self.le[rid]["value1"]): rid for rid in rows.get(("4", 0), [])}
        taught = 0
        for iv, ib in zip(S.items(self.van), S.items(self.bnw)):
            cid = M.item_content(ib.id)
            if not cid or (iv.raw[3], iv.raw[4]) == (ib.raw[3], ib.raw[4]):
                continue
            rate, spell = ib.raw[3], ib.raw[4]
            teaches = rate and spell != 0xFF and M.spell(spell) is not None and M.spell(spell) in self.sp.content_by_ability
            rid = item_rows.get(cid)
            if rid is None and teaches and item_rows:
                rid = self.new_row(next(iter(item_rows.values())))
            if rid is None:
                continue
            if teaches:
                self.set_row(rid, value1=cid, value2=rate, content_id=self.content_for_spell(spell))
            else:
                self.set_row(rid, value2=0)
            taught += 1
        if taught:
            self.log(f"  equipment spells: {taught} items changed")
        self.log(f"learning: {len(self.ch)} rows changed ({len(entries)} esper spells)")


def esper_rules(bnw):
    """BNW's esper system data for the plugin (SNES esper order)."""
    asm = open(bnw_text.SOURCE + "/asm/banks/c3.asm", encoding="utf-8", errors="replace").read()
    block = asm.split("EsperData:", 1)[1]
    masks = []
    for line in block.splitlines()[1:17]:
        bs = [int(x, 16) for x in re.findall(r"\$([0-9A-Fa-f]{2})", line.split(";")[0])]
        masks.append(bs)
    sections = bnw_text.sections()
    names = S.spell_names(bnw)
    out = []
    for e, raw in enumerate(bnw.table(*S.ESPERS)):
        users = [c for c, m in enumerate(masks) if len(m) == 4 and m[e >> 3] >> (e & 7) & 1]
        out.append({
            "snes_id": e, "name": names[54 + e], "pr_esper": M.ESPER_ORDER[e],
            # same 3 slots and rule as the learning port; ability_id = PR ability
            "spells": [{"slot": raw[j + 1], "name": names[raw[j + 1]], "sp": raw[j], "ability_id": M.spell(raw[j + 1])}
                       for j in (0, 2, 4) if raw[j + 1] != 0xff and raw[j]],
            "el_bonus": raw[10], "el_bonus_text": sections["esper_bonus"][raw[10]] if raw[10] < 16 else "",
            "equip_bonus_text": sections["equip_bonus"][e] if e < len(sections["equip_bonus"]) else "",
            "users": users,
        })
    return out
