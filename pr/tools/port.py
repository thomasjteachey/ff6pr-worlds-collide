"""Shared porter: turn a modded FF6 SNES ROM (US v1.0 layout) into a Magicite mod
for the Pixel Remaster.

Diffs the mod's ROM against vanilla, converts every changed record to the PR's
tables/text/scripts and writes pr/mods/<ModName>/. Per-mod settings (ROM path,
text source, extras) live in tools/profiles.py.

  .venv/Scripts/python tools/port.py bnw            # Brave New World
  .venv/Scripts/python tools/port.py beyond-chaos   # latest Beyond Chaos seed
  .venv/Scripts/python tools/port.py --rom X.sfc --name MyMod
"""
import json
import os
import re
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(__file__))
import ff6snes as S  # noqa: E402
import idmap as M  # noqa: E402
import port_spells as PS  # noqa: E402
import prdata as P  # noqa: E402

PROJECT = os.path.normpath(os.path.join(P.ROOT, ".."))
VANILLA_ROM = P.VANILLA_ROM
OUT = None      # set by main() from the profile
PROFILE = None
MASTER_PATH = "Assets/GameAssets/Serial/Data/Master"

GROWTH_HP, GROWTH_MP = 0x26F4A0, 0x26F502  # 98 bytes each, levels 2..99

report = []


def log(msg):
    report.append(msg)
    print(msg)


def setv(row, col, value, changed):
    value = str(value)
    if row[col] != value:
        row[col] = value
        changed.add(int(row["id"]))


# --- equip permissions -----------------------------------------------------
class JobGroups:
    """Find or create job_group rows for a set of PR jobs."""

    def __init__(self):
        self.t = P.Table("job_group")
        self.by_set = {}
        for i, r in self.t.rows.items():
            self.by_set.setdefault(self.jobs(i), i)
        self.new = set()

    def jobs(self, gid):
        r = self.t[gid]
        return frozenset(j for j in range(1, 23) if r[f"job{j}_accept"] == "1")

    def for_snes_mask(self, mask, keep_extra=frozenset()):
        js = {b + 1 for b in range(14) if mask >> b & 1}
        if js:
            js |= {15, 16, 17, 18}          # Banon, Leo, Ghost, Soldier (PR convention)
        if 1 in js:
            js.add(19)                      # Terra's opening job
        js |= set(keep_extra)
        js = frozenset(js)
        if js not in self.by_set:
            gid = max(self.t.rows) + 1
            row = {"id": str(gid)}
            for j in range(1, 23):
                row[f"job{j}_accept"] = "1" if j in js else "0"
            self.t.rows[gid] = row
            self.by_set[js] = gid
            self.new.add(gid)
        return self.by_set[js]


# SNES battle command id -> PR command id (matched by name; Morph uses the id Terra's PR row uses)
SNES_CMD = {0: 1, 1: 3, 2: 7, 3: 9, 4: 9, 5: 10, 6: 11, 7: 12, 8: 13, 9: 14, 10: 15, 11: 16, 12: 17, 13: 18,
            14: 19, 15: 20, 16: 23, 17: 24, 18: 25, 19: 22, 20: 5, 21: 2, 22: 30, 23: 31, 24: 21, 26: 27,
            27: 28, 28: 29, 29: 26}


def pr_commands(snes, vanilla_pr, unknown):
    """SNES command list -> PR command_id1..6, keeping the PR's shape: Attack, the
    hidden Magitek slot (if the character had it), specials, Magic (if the PR gave
    it up front), Items."""
    out = [1] if 0 in snes else []
    if 26 in vanilla_pr and 29 not in snes:   # MagiTek dealt as a command (WC's -com): that slot only
        out.append(26)
    for c in snes:
        if c in (0, 1, 2, 0xFF):
            continue
        if c in SNES_CMD:
            out.append(SNES_CMD[c])
        else:
            unknown[c] += 1
    if 7 in vanilla_pr and 2 in snes:
        out.append(7)
    if 1 in snes:
        out.append(3)
    return (out + [0] * 6)[:6]


def port_characters(van, bnw, tables, messages=None):
    cs = tables["character_status"]
    changed = set()
    hp_v, mp_v = van.b(GROWTH_HP, 98), van.b(GROWTH_MP, 98)
    hp_b, mp_b = bnw.b(GROWTH_HP, 98), bnw.b(GROWTH_MP, 98)
    # WC: every character starts at its start level (-stl, default 3: C0/9FC6, the immediate of
    # its level init) with no variation (it zeroes the level factors); the PR's start them at 1-3
    wc_level = bnw.data[0x9FC6] if "WC_FLAGS" in os.environ and 1 <= bnw.data[0x9FC6] <= 99 else None
    exp_at = {int(r["lv"]): int(r["total_exp"]) for r in P.Table("exp_table") if r["group_id"] == "1"}
    for cv, cb in zip(S.characters(van)[:16], S.characters(bnw)[:16]):
        row = cs[M.character(cb.id)]
        if wc_level and cb.id < 14 and int(row["lv"]) != wc_level:
            setv(row, "lv", wc_level, changed)
            setv(row, "exp", exp_at.get(wc_level, 0), changed)
        lv = int(row["lv"])
        setv(row, "strength", cb.vigor, changed)
        setv(row, "agility", cb.speed, changed)
        setv(row, "vitality", cb.stamina, changed)
        setv(row, "magic", cb.magic, changed)
        setv(row, "attack", cb.attack, changed)
        setv(row, "defense", cb.defense, changed)
        setv(row, "ability_defense", cb.mdef, changed)
        setv(row, "evasion_rate", cb.evade, changed)
        setv(row, "magic_evasion_rate", cb.mblock, changed)
        setv(row, "hp", cb.hp + sum(hp_b[:lv - 1]), changed)
        setv(row, "mp", cb.mp + sum(mp_b[:lv - 1]), changed)
        # SNES order rhand, lhand, head, body, relic, relic; the PR's content_id1-6 is rhand, lhand,
        # body, head, relic, relic, and an empty slot is its own "empty" item (93 hands, 198 body,
        # 199 head, 200 relics), not 0
        for k, (iv, ib) in enumerate(zip(cv.equip, cb.equip)):
            if iv != ib:
                slot = PR_EQUIP_SLOT[k]
                setv(row, f"content_id{slot}", M.item_content(ib) if ib != 0xff else PR_EMPTY_SLOT[slot], changed)
        if cv.commands != cb.commands:
            unknown = Counter()
            vpr = [int(row[f"command_id{k}"]) for k in range(1, 7)]
            for k, cid in enumerate(pr_commands(cb.commands, vpr, unknown), 1):
                setv(row, f"command_id{k}", cid, changed)
            for c in unknown:
                log(f"  {cb.name}: command {c} has no PR equivalent (dropped)")
        if messages is not None and cb.name and cb.name.lower() != cv.name.lower():
            messages[f"MSG_CHAR_NAME_{cb.id + 1:02d}"] = cb.name
    log(f"characters: {len(changed)} changed")

    g = tables["growth_curve"]
    gch = set()
    if hp_v == hp_b and mp_v == mp_b:
        log("growth curve: unchanged by the mod")
        return {"character_status": changed, "growth_curve": gch}
    for r in g:
        lv = int(r["lv"])
        if 2 <= lv <= 99:
            setv(r, "hp_value1", hp_b[lv - 2], gch)
            setv(r, "mp_value1", mp_b[lv - 2], gch)
    log(f"growth curve: {len(gch)} levels changed (HP {sum(a != b for a, b in zip(hp_v, hp_b))}, "
        f"MP {sum(a != b for a, b in zip(mp_v, mp_b))} differ from SNES vanilla)")
    return {"character_status": changed, "growth_curve": gch}


def SELL_DIVISOR():
    """Sell price = buy / this (0: nothing). WC's -ssf4 / -ssf8 / -ssf0 (WC_FLAGS, tools/wc_seed.py)."""
    flags = os.environ.get("WC_FLAGS", "").split()
    return 4 if "-ssf4" in flags else 8 if "-ssf8" in flags else 0 if "-ssf0" in flags else 2


def port_sell(tables):
    """WC's sell fractions for every item, weapon and armor (port_items sets the changed ones)."""
    d = SELL_DIVISOR()
    if d == 2:
        return {}
    changed = {}
    for name in ("item", "weapon", "armor"):
        t = tables.setdefault(name, P.Table(name))
        ch = changed.setdefault(name, set())
        for row in t:
            if int(row["buy"]) > 0:
                setv(row, "sell", int(row["buy"]) // d if d else 0, ch)
    log(f"sell prices: buy / {d or 'none'} ({sum(len(v) for v in changed.values())} rows)")
    return changed


def port_items(van, bnw, tables, jobs):
    changed = {"weapon": set(), "armor": set(), "item": set()}
    skipped = []
    for iv, ib in zip(S.items(van), S.items(bnw)):
        m = M.item(ib.id)
        if not m or iv.raw == ib.raw:
            continue
        if iv.type != ib.type:
            skipped.append(f"{ib.id} {iv.name}->{ib.name} type {iv.type}->{ib.type}")
            continue
        table, pid = m
        row = tables[table][pid]
        ch = changed[table]
        price = ib.price if ib.price > 2 else 0
        setv(row, "buy", price, ch)
        setv(row, "sell", price // SELL_DIVISOR() if SELL_DIVISOR() else 0, ch)
        if table == "item":
            continue
        if table == "weapon":
            setv(row, "attack", ib.power, ch)
        else:
            setv(row, "defense", ib.power, ch)
            setv(row, "ability_defense", ib.hit_or_mdef, ch)
            setv(row, "ability_evasion_rate", ib.mblock, ch)
        setv(row, "evasion_rate", ib.evade, ch)
        setv(row, "strength", ib.vigor, ch)
        setv(row, "agility", ib.speed, ch)
        setv(row, "vitality", ib.stamina, ch)
        setv(row, "magic", ib.magic, ch)
        if iv.equip_chars != ib.equip_chars:
            setv(row, "equip_job_group_id", jobs.for_snes_mask(ib.equip_chars), ch)
    for t, ch in changed.items():
        log(f"{t}: {len(ch)} changed")
    for s in skipped:
        log(f"  skipped (type changed): {s}")
    return changed


def drop_fields(items):
    rs, cs, rd, cd = items

    def ci(x):
        return 0 if x == 0xff else M.item_content(x)
    if cd != 0xff:
        drop = (100, ci(cd), ci(rd) if rd != 0xff else ci(cd))
    elif rd != 0xff:
        drop = (12, ci(rd), ci(rd))
    else:
        drop = (0, 0, 0)
    return drop, (ci(cs), ci(rs))


PR_EQUIP_SLOT = (1, 2, 4, 3, 5, 6)             # SNES equip index -> character_status content_idN
PR_EMPTY_SLOT = {1: 93, 2: 93, 3: 198, 4: 199, 5: 200, 6: 200}


def port_enemies(van, bnw, tables):
    mo = tables["monster"]
    ch = set()
    blanks, kept_hp = [], []
    for ev, eb in zip(S.enemies(van), S.enemies(bnw)):
        if ev.raw == eb.raw and ev.items == eb.items:
            continue
        if S.blank_enemy(bnw, ev.id) and not S.blank_enemy(van, ev.id):
            blanks.append(ev.id)          # the PR's battles still fight the vanilla monster there
            continue
        row = mo[ev.id + 1]
        # a 0/1 HP placeholder (BNW's protected parts and event monsters: the Whelk's and the
        # Chesticle's shells, the Guardian...): the PR's fights for them end or misbehave when it
        # dies (the Whelk's shell ends the battle: FinalAttackAct 735), so it keeps its HP
        hp = eb.hp
        if hp <= 1 < ev.hp:
            hp = ev.hp
            kept_hp.append(ev.id)
        for col, val in (("hp", hp), ("mp", eb.mp), ("exp", eb.exp), ("gill", eb.gold), ("lv", eb.level),
                         ("agility", eb.speed), ("attack", eb.vigor), ("ability_attack", eb.magic),
                         ("defense", eb.defense), ("ability_defense", eb.mdef), ("evasion_rate", eb.evade),
                         ("magic_evasion_rate", eb.mblock), ("accuracy_rate", eb.accuracy)):
            setv(row, col, val, ch)
        # undead (SNES byte 18 bit 7; WC's -bnu clears it on bosses) = the PR's species bit 0
        if (ev.raw[18] ^ eb.raw[18]) & 0x80:
            sp = int(row["species"])
            setv(row, "species", sp | 1 if eb.raw[18] & 0x80 else sp & ~1, ch)
        if ev.items != eb.items:
            (rate, d1, d2), (s1, s2) = drop_fields(eb.items)
            setv(row, "drop_rate", rate, ch)
            setv(row, "drop_content_id1", d1, ch)
            setv(row, "drop_content_id1_value", 1 if d1 else 0, ch)
            setv(row, "drop_content_id2", d2, ch)
            setv(row, "drop_content_id2_value", 1 if d2 else 0, ch)
            setv(row, "steal_content_id1", s1, ch)
            setv(row, "steal_content_id2", s2, ch)
    log(f"monster: {len(ch)} changed; {len(blanks)} slots BNW emptied keep the PR's monster "
        f"(SNES {', '.join(map(str, blanks))}); {len(kept_hp)} placeholders keep their HP (SNES {', '.join(map(str, kept_hp))})")
    return ch


MESSAGE_PATH = "Assets/GameAssets/Serial/Data/Message"
AP_TABLE = 0x1FB400  # magic points per formation (512 bytes) -> monster_party.get_value
SHOPS = 0x047AC0     # 128 shops x 9 bytes: type, 8 items


def port_ap(van, bnw, tables):
    mp = tables.setdefault("monster_party", P.Table("monster_party"))
    ch = set()
    for pid, f in M.party_formation().items():
        if f >= 512:
            continue
        a, b = van.data[AP_TABLE + f], bnw.data[AP_TABLE + f]
        if a != b and pid in mp.rows:
            setv(mp[pid], "get_value", b, ch)
    log(f"monster_party AP: {len(ch)} formations changed")
    return ch


def port_escape(van, bnw, tables):
    """Escapable battles: a SNES enemy's "can't run" (byte 19 bit 3; WC's -escr randomizes it)
    makes its formation's PR party not_escape 2 (0 = can run; 3, the PR's special ones, stays).
    Only parties whose answer changed from vanilla's are touched."""
    mp = tables.setdefault("monster_party", P.Table("monster_party"))
    ev, eb = S.enemies(van), S.enemies(bnw)
    ch = set()

    def norun(enemies, mons):
        return any(enemies[m - 1].raw[19] & 0x08 for m in mons if 0 < m <= len(enemies))

    for pid, r in mp.rows.items():
        if pid > 576:
            continue   # the PR's own parties (Coliseum copies and later)
        mons = [int(r[f"monster{i}"]) for i in range(1, 10) if int(r[f"monster{i}"])]
        if not mons or r["not_escape"] == "3":
            continue
        a, b = norun(ev, mons), norun(eb, mons)
        if a != b:
            setv(mp[pid], "not_escape", 2 if b else 0, ch)
    log(f"monster_party escape: {len(ch)} parties changed")
    return ch


def port_event_formations(van, bnw, tables):
    """Event formations a WC seed changes in place (the Marshal's Lobos: formation 4, unless
    -bmkl): its PR party and the PR's copies of it (the same line-up: 5 and 596) take the new
    monsters slot for slot (positions kept). WC seeds only (WC_FLAGS set by tools/wc_seed.py):
    BNW's formation edits stay as they are."""
    if "WC_FLAGS" not in os.environ:
        return set()
    import port_encounters as E
    mp = tables.setdefault("monster_party", P.Table("monster_party"))
    ch = set()
    done = []
    for f in range(576):
        a = [e for e, _, _ in E.formation(van, f)]
        b = [e for e, _, _ in E.formation(bnw, f)]
        pid = M.party(f)
        if a == b or len(a) != len(b) or pid not in mp.rows:
            continue
        base = E.party_members(mp[pid])
        for p in [q for q in mp.rows if q < 1000 and E.party_members(mp[q]) == base]:
            row, used = mp[p], set()
            for old, new in zip(a, b):
                if old == new:
                    continue
                for k in range(1, 10):
                    if k not in used and row[f"monster{k}"] == str(old + 1):
                        setv(row, f"monster{k}", new + 1, ch)
                        used.add(k)
                        break
            done.append(p)
    log(f"event formations: parties {done} take the seed's monsters" if done else "event formations: none changed")
    return ch


# WC's -cspr / -cpor (character sprites / portraits): WC sprite id -> the PR character whose art
# it is (0-13 the party, 14 Soldier = Biggs/Wedge's, 16 Leo, 17 Banon, 20 Ghost, 21 Kefka;
# Imp / Esper Terra / Merchant have no PR art of their own and keep the character's). Portrait ids:
# 0-13, 15 Leo, 16 Banon, 17 Ghost, 18 Wedge/Vicks.
WC_SPRITE_ART = {**{i: i + 1 for i in range(14)}, 14: 31, 16: 16, 17: 15, 20: 17, 21: 33}
WC_PORTRAIT_ART = {**{i: i + 1 for i in range(14)}, 15: 16, 16: 15, 17: 17, 18: 31}


def port_wc_graphics(tables):
    flags = os.environ.get("WC_FLAGS", "").split()
    ca = tables.setdefault("character_asset", P.Table("character_asset"))
    vca = P.Table("character_asset")   # the art to copy: vanilla's (swaps read before they write)
    cs = P.Table("character_status")
    ch = set()

    def own_rows(char, kind, table=ca):
        job = cs[char]["job_id"]
        return [r for r in table if r["character_id"] == str(char) and r["job_id"] == job and r["asset_type"] == kind]

    for flag, kind, art in (("-cspr", "1", WC_SPRITE_ART), ("-cpor", "2", WC_PORTRAIT_ART)):
        if flag not in flags or flags.index(flag) + 1 >= len(flags):
            continue
        ids = [int(x) for x in flags[flags.index(flag) + 1].split(".") if x.isdigit()]
        done = []
        for c, wid in enumerate(ids[:14]):
            donor = art.get(wid)
            if donor is None or donor == c + 1:
                continue
            src = own_rows(donor, kind, vca)
            if not src:
                continue
            for r in own_rows(c + 1, kind):
                for col in ("asset_name", "size_type", "field_character_asset_id") if kind == "1" else ("asset_name",):
                    setv(ca.rows[int(r["id"])], col, src[0][col], ch)
            done.append(f"{c + 1}<-{donor}")
        log(f"{flag}: {' '.join(done) or 'no PR art changes'}")
    return {"character_asset": ch} if ch else {}


def port_shops(van, bnw, tables):
    pr = tables.setdefault("product", P.Table("product"))
    # Groups 83-92 are the PR's half-price shop copies (Edgar at Figaro and the other
    # character/shop discounts).  Their coefficient is a fixed negative gil amount, tied to
    # the product that originally occupied that row.  Randomizing only content_id can therefore
    # make a cheap replacement negative (the user's seed: 129-gil Dried Meat plus the old
    # product's -500 = -371).  Remember which groups are discounted; recalculate every row after
    # stock and seed prices have both been applied.
    discount_groups = {int(r["group_id"]) for r in pr if int(r["coefficient"]) < 0}
    groups = {}
    for r in pr:
        groups.setdefault(int(r["group_id"]), []).append(int(r["id"]))

    def items(rom, s):
        raw = rom.data[SHOPS + 9 * s:SHOPS + 9 * s + 9]
        return [M.item_content(x) for x in raw[1:] if x != 0xff]

    # match vanilla SNES shops to PR groups by stock overlap
    scored = []
    for s in range(128):
        want = set(items(van, s))
        if want:
            for g, ids in groups.items():
                have = {int(pr[i]["content_id"]) for i in ids}
                scored.append((len(want & have) / len(want | have), s, g))
    # ties (stock several shops share, e.g. Albrook's and Vector's weapons): a town's shops are
    # neighbours in both games, so the PR group next to where the SNES shop's neighbours went wins
    cands = {}
    for score, s, g in scored:
        if score >= 0.75:
            cands.setdefault(s, []).append(g)
    anchor = {s: gs[0] for s, gs in cands.items() if len(gs) == 1 and sum(g == gs[0] for v in cands.values() for g in v) == 1}

    def near(s, g):
        d = [abs(g - (anchor[s + k] - k)) for k in (-1, 1, -2, 2, -3, 3) if s + k in anchor]
        return min(d) if d else 99

    scored = [(score, -near(s, g), s, g) for score, s, g in scored]
    scored.sort(reverse=True)
    scored = [(score, s, g) for score, _, s, g in scored]
    match, used, best = {}, set(), {}
    copies = []
    for score, s, g in scored:
        if score >= 0.75 and s not in match and g not in used:
            match[s], _ = g, used.add(g)
            best[s] = score
        elif s in match and g not in used and score >= 0.75 and score == best[s]:
            # another PR group with this shop's very stock (a town's WoB / WoR copies): it is this
            # shop's copy, before a weaker match elsewhere claims it (South Figaro WoR's item
            # shop, PR 67 and 92: SNES shop 71 took 92 at 0.78 and 92 kept vanilla stock)
            copies.append((s, g))
            used.add(g)
    # the PR's second copies of a shop (a town's two versions, WoB / WoR, as separate groups):
    # a group left over that still matches a SNES shop takes that shop's stock too
    pairs = list(match.items()) + copies
    for score, s, g in scored:
        if score >= 0.75 and g not in used:
            pairs.append((s, g))
            used.add(g)
    port_shops.pairs = pairs
    ch = set()
    next_id = max(pr.rows) + 1
    template = next(iter(pr))
    for s, g in pairs:
        new = items(bnw, s)
        if new == items(van, s):
            continue
        ids = sorted(groups[g])
        for n, cid in enumerate(new):
            if n < len(ids):
                setv(pr[ids[n]], "content_id", cid, ch)
            else:
                pr.rows[next_id] = dict(template, id=str(next_id), content_id=str(cid), group_id=str(g),
                                        coefficient="0", purchase_limit="0")
                ch.add(next_id)
                next_id += 1
        for rid in ids[len(new):]:
            setv(pr[rid], "group_id", 9999, ch)  # partials can't delete: park extra rows in an unused shop

    content = tables.setdefault("content", P.Table("content"))
    sale_tables = {1: tables.setdefault("item", P.Table("item")),
                   2: tables.setdefault("weapon", P.Table("weapon")),
                   3: tables.setdefault("armor", P.Table("armor"))}
    for row in pr:
        if int(row["group_id"]) not in discount_groups:
            continue
        c = content[int(row["content_id"])]
        price = int(sale_tables[int(c["type_id"])][int(c["type_value"])]["buy"])
        setv(row, "coefficient", price // 2 - price, ch)  # final cost = floor(seed price / 2)
    log(f"shops: {len(match)} SNES shops matched to PR shops (+{len(pairs) - len(match)} PR copies); {len(ch)} product rows changed")
    return ch


def port_item_text(van, bnw, messages):
    """The mod's item names and descriptions onto the PR item/weapon/armor text keys
    (only items whose name or description the mod changed)."""
    import re
    vmsg = P.messages()
    content = P.Table("content")
    secs = PROFILE.descriptions() or {}
    descs = secs.get("items", [])
    vdescs = [] if PROFILE.rename_all else PROFILE.vanilla_descriptions().get("items", [])
    names = S.items(bnw)
    vnames = S.items(van)
    n = 0
    for it, vit in zip(names, vnames):
        m = M.item(it.id)
        if not m or not it.name:
            continue
        desc_changed = it.id < len(descs) and (it.id >= len(vdescs) or descs[it.id] != vdescs[it.id])
        if it.name == vit.name and not desc_changed and not PROFILE.rename_all:
            continue
        row = content[M.content_id(*m)]
        if row["mes_id_name"] in ("None", ""):
            continue
        old = vmsg.get(row["mes_id_name"], "")
        icon = re.match(r"(<IC_[A-Z]+>)", old)
        if PROFILE.rename_all or it.name != vit.name:
            messages[row["mes_id_name"]] = (icon.group(1) if icon else "") + it.name
        desc = descs[it.id] if desc_changed else ""
        if desc and row["mes_id_description"] not in ("None", ""):
            messages[row["mes_id_description"]] = " ".join(desc.split("\n"))
        n += 1
    log(f"item text: {n} names/descriptions")


def write_messages(messages, name="system_en", extra=None):
    """messages -> message/<name>.txt; extra {file name: {key: text}} -> more text files
    (battle lines: story_mes_en / story_cha_en), all listed in keys/mod_message.json."""
    group_dir = os.path.join(OUT, "message")
    files = {name: messages}
    files.update({k: v for k, v in (extra or {}).items() if v})
    for fname, lines in files.items():
        path = os.path.join(group_dir, *MESSAGE_PATH.split("/"), fname + ".txt")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="") as f:
            for k in sorted(lines):
                f.write(f"{k}\t{lines[k]}\r\n")
        log(f"wrote {len(lines)} text lines to message/{fname}.txt")
    os.makedirs(os.path.join(group_dir, "keys"), exist_ok=True)
    with open(os.path.join(group_dir, "keys", "mod_message.json"), "w", encoding="utf-8") as f:
        json.dump({"keys": list(files), "values": [f"{MESSAGE_PATH}/{n}" for n in files]}, f, indent=2)


def write_mod(tables, changed):
    group_dir = os.path.join(OUT, "master")
    keys, values = [], []
    for name, ids in changed.items():
        if not ids:
            continue
        path = os.path.join(group_dir, *MASTER_PATH.split("/"), name + ".csv")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(tables[name].to_csv(ids))
        keys.append(name)
        values.append(f"{MASTER_PATH}/{name}")
    os.makedirs(os.path.join(group_dir, "keys"), exist_ok=True)
    with open(os.path.join(group_dir, "keys", "mod_master.json"), "w", encoding="utf-8") as f:
        json.dump({"keys": keys, "values": values}, f, indent=2)
    log(f"wrote {len(keys)} tables to {group_dir}")


# The PR stops these stats at 128 (game_constant_int.csv); the SNES engine at 255. Monster
# column that can go past the cap (the PR keeps monster Speed in agility and magic power in
# ability_attack; vanilla PR monsters stay at 99 and 55).
STAT_CAPS = {"MAX_STRNGTH": None, "MAX_VITALITY": None, "MAX_AGILITY": "agility", "MAX_MAGIC": None,
             "MAX_ABILITY_ATTACK": "ability_attack"}


def battle_constants(tables, profile, log):
    """profile.game_constants: {key_name: (value1, value2)} written over game_constant_int.csv
    (encounter-type odds, starting ATB ranges...)."""
    want = getattr(profile, "game_constants", None) or {}
    if not want:
        return {}
    consts = tables.setdefault("game_constant_int", P.Table("game_constant_int"))
    key_id = {r["key_name"]: int(r["id"]) for r in consts}
    changed = set()
    for key, (v1, v2) in want.items():
        row = consts[key_id[key]]
        if (row["value1"], row["value2"]) != (str(v1), str(v2)):
            row["value1"], row["value2"] = str(v1), str(v2)
            changed.add(key_id[key])
    log(f"battle constants: {len(changed)} changed")
    return {"game_constant_int": changed} if changed else {}


def stat_caps(tables, profile, log):
    """Raise the PR's stat caps to the profile's stat_cap, or to 255 where a monster needs it."""
    consts = tables.setdefault("game_constant_int", P.Table("game_constant_int"))
    key_id = {r["key_name"]: int(r["id"]) for r in consts}
    monsters = tables.get("monster") or P.Table("monster")
    changed, raised = set(), []
    for key, col in STAT_CAPS.items():
        row = consts[key_id[key]]
        cap = int(row["value1"])
        want = getattr(profile, "stat_cap", None) or 0
        if col and any(int(m[col]) > cap for m in monsters):
            want = max(want, 255)
        if want > cap:
            row["value1"] = str(want)
            changed.add(key_id[key])
            raised.append(f"{key} {cap}->{want}")
    log(f"stat caps: {', '.join(raised) if raised else 'unchanged (128)'}")
    return {"game_constant_int": changed} if changed else {}


def main(profile):
    global OUT, PROFILE
    PROFILE, OUT = profile, profile.out
    import shutil
    if os.path.isdir(OUT):
        shutil.rmtree(OUT)
    log(f"porting {profile.rom} -> {OUT}")
    van, bnw = S.Rom(VANILLA_ROM), S.Rom(profile.rom)
    M.SPELL_OVERRIDES.clear()
    M.SPELL_OVERRIDES.update(getattr(profile, "spell_relocations", None) or {})
    if M.SPELL_OVERRIDES:
        log(f"spells relocated to abilities of their own: {M.SPELL_OVERRIDES}")
    names = ["character_status", "growth_curve", "weapon", "armor", "item", "monster"]
    tables = {n: P.Table(n) for n in names}
    jobs = JobGroups()
    changed = {}
    messages = {}
    changed.update(port_characters(van, bnw, tables, messages))
    changed.update(port_items(van, bnw, tables, jobs))
    for k, v in port_sell(tables).items():
        changed.setdefault(k, set()).update(v)
    changed["monster"] = port_enemies(van, bnw, tables)
    tables["job_group"] = jobs.t
    changed["job_group"] = jobs.new
    log(f"job_group: {len(jobs.new)} new equip groups")

    sp = PS.SpellPorter(van, bnw, tables, messages, log, profile)
    sp.port()
    PS.LearningPorter(van, bnw, sp, log).port()
    port_item_text(van, bnw, messages)
    changed["monster_party"] = port_ap(van, bnw, tables) | port_escape(van, bnw, tables) | port_event_formations(van, bnw, tables)
    changed["product"] = port_shops(van, bnw, tables)
    import port_chests
    for name, ids in port_chests.port(van, bnw, OUT, log, tables).items():
        changed.setdefault(name, set()).update(ids)
    import port_merchants   # after port_chests: it adds NPCs to the entity files that wrote
    for name, ids in port_merchants.port(van, bnw, tables, OUT, log, profile).items():
        changed.setdefault(name, set()).update(ids)
    import port_effects
    for name, ids in port_effects.EffectPorter(van, bnw, tables, log, profile).port().items():
        changed.setdefault(name, set()).update(ids)
    for name, ids in port_effects.port_rages(van, bnw, tables, log, profile.rage_odds).items():
        changed.setdefault(name, set()).update(ids)
    tuning = getattr(profile, "condition_tuning", None) or {}
    if tuning:
        cond = tables.setdefault("condition", P.Table("condition"))
        for cid, cols in tuning.items():
            for col, val in cols.items():
                if cond[cid][col] != val:
                    cond[cid][col] = val
                    changed.setdefault("condition", set()).add(cid)
        log(f"status tuning: {len(changed.get('condition', ()))} conditions changed")
    import port_specials
    for name, ids in port_specials.port(van, bnw, tables, messages, log, profile).items():
        changed.setdefault(name, set()).update(ids)
    import port_flags
    for name, ids in port_flags.port(van, bnw, tables, log, profile).items():
        changed.setdefault(name, set()).update(ids)
    import port_skills
    for name, ids in port_skills.port(van, bnw, tables, log).items():
        changed.setdefault(name, set()).update(ids)
    import port_dances
    if "-dda" in os.environ.get("WC_FLAGS", "").split() or getattr(profile, "dance_descriptions", False):
        log(f"dances: {port_dances.describe_steps(bnw, messages)} dance descriptions list their steps")
    for name, ids in port_dances.port(van, bnw, tables, log, profile, messages).items():
        changed.setdefault(name, set()).update(ids)
    import port_procs
    for name, ids in port_procs.ProcPorter(van, bnw, tables, sp, log, profile).port(OUT).items():
        changed.setdefault(name, set()).update(ids)
    import port_tools
    for name, ids in port_tools.port(van, bnw, tables, sp, log, profile).items():
        changed.setdefault(name, set()).update(ids)
    import port_slots
    for name, ids in port_slots.port(van, bnw, tables, sp, log, profile).items():
        changed.setdefault(name, set()).update(ids)
    for aid, cols in (getattr(profile, "ability_overrides", None) or {}).items():
        row = sp.ab.rows[aid]
        for col, val in cols.items():
            if row[col] != str(val):
                row[col] = str(val)
                changed.setdefault("ability", set()).add(aid)
        log(f"ability {aid}: {cols}")
    # WC's -fst (SwdTech gauge charges four times faster): the PR's Bushido charges for its
    # technique's ability_wait (Fang 3 .. Oblivion 17): a quarter of it
    if "-fst" in os.environ.get("WC_FLAGS", "").split():
        for aid in range(124, 132):
            row = sp.ab.rows[aid]
            want = str(max(1, round(int(row["ability_wait"]) / 4)))
            if row["ability_wait"] != want:
                row["ability_wait"] = want
                changed.setdefault("ability", set()).add(aid)
        log("-fst: Bushido charge times / 4")
    # WC's -scis (Sketch & Control 100% accurate): Control (ability 13) is an add-condition
    # ability whose hit roll runs only below accuracy 255 (CalcExecuteFF6.AddConditionExection,
    # decompiled tools/decomp/battle: rate = min(accuracy, 255); the roll only if rate < 255).
    # Immunity, bosses and the can't-be-controlled list still decide first, as in WC. Sketch's
    # roll is the plugin's (WcChallenges: IsResistanceSketch)
    if "-scis" in os.environ.get("WC_FLAGS", "").split():
        row = sp.ab.rows[13]
        if row["accuracy_rate"] != "255":
            row["accuracy_rate"] = "255"
            changed.setdefault("ability", set()).add(13)
        log("-scis: Control always hits a target it can control (accuracy 255)")
    # WC's -llr (level lores' levels random): its Lv. N divisor (lore ability data, 0x04725A + 14 x
    # lore, byte 8; 0 = "?") onto the PR's level programs: "LEVEL" + N (the game reads the number:
    # its code holds only the strings "LEVEL", "LEVEL5" and "LEVEL_RANDOM_HOLY", yet LEVEL3/4 work)
    # or LEVEL_RANDOM_HOLY for "?"; the name shows the new level
    if "-llr" in os.environ.get("WC_FLAGS", "").split():
        progs = {1: "LEVEL1", 2: "LEVEL2", 3: "LEVEL3", 4: "LEVEL4", 5: "LEVEL5", 0: "LEVEL_RANDOM_HOLY"}
        names_now = P.messages()
        content4 = {int(r["type_value"]): r for r in P.Table("content") if r["type_id"] == "4"}
        done = []
        for lore in (9, 10, 11, 13):   # Lv.5 Doom, Lv.4 Flare, Lv.3 Muddle, Lv.? Pearl
            at = 0x04725A + 14 * lore + 8
            v, b = van.data[at], bnw.data[at]
            aid = 140 + lore
            if v == b:
                continue
            if b not in progs:
                done.append(f"{aid}: Lv.{b} (no PR program, kept)")
                continue
            row = sp.ab.rows[aid]
            if row["process_prog"] != progs[b]:
                row["process_prog"] = progs[b]
                changed.setdefault("ability", set()).add(aid)
            key = content4[aid]["mes_id_name"]
            text = messages.get(key, names_now.get(key, ""))
            messages[key] = re.sub(r"Lv\. ?[0-9?]", f"Lv. {b if b else '?'}", text, count=1)
            done.append(f"{aid}: Lv.{b if b else '?'}")
        log(f"-llr: {', '.join(done) or 'unchanged'}")
    # WC's -scan: Scan (the PR's Libra, ability 64) costs no MP (the plugin makes every enemy
    # scannable and teaches it at the start)
    if "-scan" in os.environ.get("WC_FLAGS", "").split() and sp.ab.rows[64]["use_value"] != "0":
        sp.ab.rows[64]["use_value"] = "0"
        changed.setdefault("ability", set()).add(64)
        log("-scan: Libra costs 0 MP")
    # WC's -ond: the party menus show each character's original name (WC draws it under the
    # name), for telling who is who once -name or -cspr changed them; the PR's menus show a job
    # title there, which becomes the original name for each character the seed renamed or gave
    # another's sprite (the others keep their title: both presets set -ond)
    wc_flags = os.environ.get("WC_FLAGS", "").split()
    if "-ond" in wc_flags:
        def arg(flag):
            return wc_flags[wc_flags.index(flag) + 1].split(".") if flag in wc_flags[:-1] else []
        new_names, sprites = arg("-name"), arg("-cspr")
        names = P.messages("system", "en")    # vanilla's: the seed's name keys carry -name's
        cs, jobs = P.Table("character_status"), P.Table("job")
        shown = []
        for cid in range(1, 15):
            original = names.get(f"MSG_CHAR_NAME_{cid:02d}", "")
            renamed = cid <= len(new_names) and new_names[cid - 1] not in ("", original)
            reskinned = cid <= len(sprites) and sprites[cid - 1].isdigit() and int(sprites[cid - 1]) != cid - 1
            job = jobs.rows.get(int(cs[cid]["job_id"]))
            if job and original and (renamed or reskinned):
                messages[job["mes_id_name"]] = original
                shown.append(original)
        log(f"-ond: original names in place of {len(shown)} job titles ({', '.join(shown)})")
    for name, ids in port_wc_graphics(tables).items():
        changed.setdefault(name, set()).update(ids)
    import port_sprites
    for name, ids in port_sprites.port(van, bnw, tables, log, profile).items():
        changed.setdefault(name, set()).update(ids)
    import port_encounters
    encounters = port_encounters.EncounterPorter(van, bnw, tables, log)
    for name, ids in encounters.port().items():
        changed.setdefault(name, set()).update(ids)
    import port_veldt   # the Veldt's formations: after the encounters, whose parties it fights
    for name, ids in port_veldt.port(van, bnw, tables, encounters, OUT, log).items():
        changed.setdefault(name, set()).update(ids)
    if getattr(profile, "event_fights", False):
        import port_events   # BNW's own story fights (its packs' formations), after the chests' boxes
        for name, ids in port_events.port(van, bnw, tables, encounters, OUT, log).items():
            changed.setdefault(name, set()).update(ids)
    import port_ai
    for name, ids in port_ai.port(van, bnw, tables, OUT, log, getattr(profile, "battle_messages", False)).items():
        changed.setdefault(name, set()).update(ids)
    for name, ids in battle_constants(tables, profile, log).items():
        changed.setdefault(name, set()).update(ids)
    for name, ids in stat_caps(tables, profile, log).items():
        changed.setdefault(name, set()).update(ids)
    for name, ids in sp.changed.items():
        changed.setdefault(name, set()).update(ids)
    import port_resist
    for name, ids in port_resist.ResistPorter(van, bnw, tables, log).port().items():
        changed.setdefault(name, set()).update(ids)
    os.makedirs(OUT, exist_ok=True)
    for fname, data in (profile.extra_data(bnw) or {}).items():
        with open(os.path.join(OUT, fname), "w", encoding="utf-8") as f:
            json.dump(data, f, indent=1)
    messages.update(profile.extra_messages(bnw) or {})

    write_mod(tables, changed)
    battle = getattr(port_ai.port, "battle_texts", None) or {}
    write_messages(messages, extra={"story_mes_en": battle, "story_cha_en": {k: "" for k in battle}})
    with open(os.path.join(OUT, "port_report.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(report) + "\n")


if __name__ == "__main__":
    import argparse
    import profiles
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("profile", nargs="?", default="bnw", choices=sorted(profiles.PROFILES))
    ap.add_argument("--rom", help="modded ROM (overrides the profile's)")
    ap.add_argument("--name", help="Magicite mod folder name (with --rom)")
    a = ap.parse_args()
    prof = profiles.PROFILES[a.profile]
    if a.rom:
        prof = profiles.Profile()
        prof.rom, prof.mod_name = a.rom, a.name or "CustomMod"
    if not prof.rom or not os.path.exists(prof.rom):
        sys.exit(f"ROM not found for profile {a.profile}: {prof.rom}")
    main(prof)
