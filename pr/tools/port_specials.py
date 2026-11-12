"""Monster special attacks (SNES enemy byte 31 + the special-attack name table).

The PR has one ability per distinct special (type 3, named MSG_MON_ATK_nn), in its own order
and shared by every monster whose special is the same: 390 "Hit" serves some 40 bosses. It
is NOT 377 + monster index (that held for 25 monsters; using it rewrote other monsters'
specials and the PR's event abilities 696 "do nothing" and 735 "everyone vanishes", which
broke the Whelk - 2026-11-07). pr_specials() reads each monster's special from its own data,
plan() decides which ability carries BNW's special for each monster: the shared one rebuilt
in place when every monster sharing it gets the same BNW byte, else a copy of it for the
monsters BNW changed (COPY_BASE + n; their compiled AI, Rage and Sketch use it). The PR's
own versions are hand-made and don't follow the SNES byte, so where BNW changed a monster's
byte the ability is rebuilt from the SNES meaning (vanilla handler C2/3302, BNW keeps every
bit; c2.asm:2256 SpecialAttStam):
  bit 7      unblockable           -> content_flag_group_id 1 (can't be dodged)
  bit 6      no damage (status only) -> no damage functions, system 5
  0x00-0x1F  inflict status n       -> function 9 (value1 = PR condition id)
  0x20-0x2F  damage x(1 + (n-0x1F)/2) -> damage; the multiplier is exported for the plugin
  0x30/0x31  drain HP / MP          -> function 13 (+77 for MP), as the PR's drains
  0x32-0x3F  remove Reflect         -> left as the PR has it
Damage = functions 2 (physical) + 23 (special attack), system 3. Monsters whose byte BNW
kept keep the PR's version. Names follow the profile's text policy (game keys only).
"""
from collections import Counter

import ff6snes as S
import port_resist as R
import prdata as P

SPECIAL_NAMES = 0x0FD0D0, 10, 384  # CF/D0D0
COPY_BASE = 7700   # copies of a shared special for monsters BNW gave a different one
DAMAGE = {(2, 0, 0, 0), (23, 0, 0, 0)}
DRAIN_HP = {(13, 1, 100, 1)}
DRAIN_MP = {(13, 2, 100, 1), (77, 2, 0, 0)}  # the PR's own MP-drain special
UNBLOCKABLE_FLAG = "1"
LAST = {}  # {"multipliers": {ability id: BNW multiplier / vanilla's}} for the plugin


_SPECIALS = None
_PLANS = {}


def pr_specials():
    """{SNES monster e: the vanilla PR ability of its special attack}, for monsters that have one.

    A special is a type-3 ability named MSG_MON_ATK_nn. The monster's AI script and its Control
    (Relm) group name it: the one such ability they use, or the Control group's one when the AI
    uses others too (Siegfried's two). Rage groups often hold a second skill instead, so they
    don't count. Monsters with none (their special is made of other abilities: Inviz) are left
    out."""
    global _SPECIALS
    if _SPECIALS is not None:
        return _SPECIALS
    import ai_corpus
    ab, mo = P.Table("ability"), P.Table("monster")
    groups = {}
    for r in P.Table("ability_random_group"):
        groups.setdefault(int(r["group_id"]), []).append(int(r["ability_id"]))
    scripts = P.Table("monster_ai_script")
    keys = {int(r["type_value"]): r["mes_id_name"] for r in P.Table("content") if r["type_id"] == "4"}

    def special(a):
        return a in ab.rows and ab[a]["type_id"] == "3" and keys.get(a, "").startswith("MSG_MON_ATK_")

    out = {}
    for e in range(384):
        m = mo.rows.get(e + 1)
        if m is None:
            continue
        name = scripts.rows.get(int(m["script_id"]), {}).get("script_name")
        doc = ai_corpus.load_pr(name) if name else None
        ai = {a for x in (doc or {}).get("Mnemonics", []) if x["mnemonic"].endswith("Act") or x["mnemonic"] == "ActForTarget"
              for a in x["operands"]["iValues"] if special(a)}
        control = {a for a in groups.get(int(m["release_ability_random_group_id"]), []) if special(a)}
        found = ai | control
        if len(found) == 1:
            out[e] = found.pop()
        elif len(control) == 1:
            out[e] = next(iter(control))
    _SPECIALS = out
    return out


def plan(van, bnw):
    """{SNES monster e: PR ability carrying its BNW special}, plus what to write:
    (owner, {ability: (template ability, BNW byte, [monsters])}). An ability shared by monsters
    BNW treats alike is rebuilt in place; otherwise each BNW byte that changed gets its own copy
    (COPY_BASE + n, in a fixed order) and the shared one stays the PR's."""
    key = (id(van), id(bnw))
    if key in _PLANS:
        return _PLANS[key]
    specials = pr_specials()
    ve, be = S.enemies(van), S.enemies(bnw)
    sharers = {}
    for e, a in specials.items():
        sharers.setdefault(a, []).append(e)
    owner, writes, n = dict(specials), {}, 0
    for a in sorted(sharers):
        es = sorted(sharers[a])
        changed = {}
        for e in es:
            if ve[e].raw[31] == be[e].raw[31] or (S.blank_enemy(bnw, e) and not S.blank_enemy(van, e)):
                continue                   # BNW kept it (or emptied the slot: the PR's monster stays)
            if build(be[e].raw[31]) is None:
                continue                   # a byte the rebuild doesn't cover: the PR's stays
            # grouped by vanilla's multiplier too: the plugin's correction is per ability
            changed.setdefault((be[e].raw[31], multiplier(ve[e].raw[31])), []).append(e)
        if not changed:
            continue
        if len(changed) == 1 and sum(map(len, changed.values())) == len(es):
            ((byte, _), who), = changed.items()
            writes[a] = (a, byte, who)     # every monster sharing it: rebuilt in place
            continue
        for (byte, _), who in sorted(changed.items()):
            cid = COPY_BASE + n
            n += 1
            writes[cid] = (a, byte, who)
            for e in who:
                owner[e] = cid
    _PLANS[key] = (owner, writes)
    return owner, writes


def special_of(van, bnw, e, default=None):
    """The PR ability monster e's BNW special uses (default when the PR gives it none)."""
    return plan(van, bnw)[0].get(e, default)


def multiplier(b):
    """Damage multiplier of a SNES special byte (1 if it isn't a 0x20-0x2F damage special)."""
    n = b & 0x3F
    return 1 + (n - 0x1F) / 2 if 0x20 <= n < 0x30 and not b & 0x40 else 1.0


def status_condition(n):
    """SNES status bit (0-31) -> PR condition id, from the porter's learned tables."""
    for table in (R.ENEMY_IMMUNE, R.PROTECT, R.ENEMY_INITIAL):
        if n in table and table[n]:
            return table[n][0]
    return None


def build(b):
    """SNES special byte -> (functions, system_id, unblockable, multiplier) or None."""
    n, no_damage, unblockable = b & 0x3F, b >> 6 & 1, b >> 7 & 1
    funcs, mult = set(), 1.0
    if n < 0x20:
        cond = status_condition(n)
        if cond is None:
            return None
        funcs.add((9, cond, 0, 0))
        if not no_damage:
            funcs |= DAMAGE
    elif n < 0x30:
        if no_damage:
            funcs = set()  # no damage and no status: does nothing (BNW 0x60/0x61)
        else:
            funcs = set(DAMAGE)
            mult = 1 + (n - 0x1F) / 2
    elif n == 0x30:
        funcs = DAMAGE | DRAIN_HP
    elif n == 0x31:
        funcs = set(DRAIN_MP)
    else:
        return None
    system = "5" if no_damage or n == 0x31 else "3"
    return funcs, system, unblockable, mult


class FunctionGroups:
    def __init__(self, tables):
        self.fg = tables.setdefault("function_group", P.Table("function_group"))
        self.index = {}
        for r in self.fg:
            self.index.setdefault(self.funcs(r), int(r["id"]))
        self.changed = set()

    @staticmethod
    def funcs(row):
        return frozenset((int(row[f"function{k}_id"]), int(row[f"function{k}_value1"]), int(row[f"function{k}_value2"]),
                          int(row[f"function{k}_value3"])) for k in (1, 2, 3) if row[f"function{k}_id"] != "0")

    def get(self, funcs):
        funcs = frozenset(funcs)
        if not funcs:
            return 0
        if funcs in self.index:
            return self.index[funcs]
        gid = max(self.fg.rows) + 1
        row = {"id": str(gid)}
        for k, f in enumerate(sorted(funcs), 1):
            for c, v in zip(("id", "value1", "value2", "value3"), f):
                row[f"function{k}_{c}"] = str(v)
        for k in range(len(funcs) + 1, 4):
            for c in ("id", "value1", "value2", "value3"):
                row[f"function{k}_{c}"] = "0"
        self.fg.rows[gid] = row
        self.index[funcs] = gid
        self.changed.add(gid)
        return gid


def port(van, bnw, tables, messages, log, profile=None):
    ab = tables.setdefault("ability", P.Table("ability"))
    content = tables.setdefault("content", P.Table("content"))
    groups = FunctionGroups(tables)
    name_key = {int(r["type_value"]): r["mes_id_name"] for r in content if r["type_id"] == "4"}
    game_text = P.messages()
    ve, be = S.enemies(van), S.enemies(bnw)
    specials = pr_specials()
    owner, writes = plan(van, bnw)
    changed, mults, new_content = set(), {}, set()
    for aid, (template, byte, who) in sorted(writes.items()):
        funcs, system, unblockable, mult = build(byte)
        if aid != template and aid in ab.rows:
            raise ValueError(f"special copy {aid} would overwrite an ability")
        row = dict(ab[template])
        row["id"] = str(aid)
        row["battle_function_group_id"] = str(groups.get(funcs))
        row["system_id"] = system
        if unblockable:
            row["content_flag_group_id"] = UNBLOCKABLE_FLAG
        # the PR applies vanilla's multiplier itself, keyed on the monster (seen: BNW's
        # Phalanx, vanilla x2 / BNW x3, hit for x2); the plugin adds BNW's on top of it
        ratio = multiplier(byte) / multiplier(ve[who[0]].raw[31])
        if abs(ratio - 1.0) > 1e-6:
            mults[aid] = round(ratio, 4)
        if row != ab.rows.get(aid):
            ab.rows[aid] = row
            changed.add(aid)
        if aid != template and aid not in name_key:
            # a copy shows the shared special's name (its content row's text key)
            src = next(r for r in content if r["type_id"] == "4" and r["type_value"] == str(template))
            cid = max(content.rows) + 1
            content.rows[cid] = dict(src, id=str(cid), type_value=str(aid))
            name_key[aid] = src["mes_id_name"]
            new_content.add(cid)
    LAST.clear()
    LAST.update(multipliers=mults)
    # names (only keys the game has text for): a special renamed when every monster sharing it
    # has the same BNW name (copies keep the shared name: their text key is the shared one)
    rename_all = getattr(profile, "rename_all", False)
    vn = [S.decode_name(x).strip() for x in van.table(*SPECIAL_NAMES)]
    bn = [S.decode_name(x).strip() for x in bnw.table(*SPECIAL_NAMES)]
    sharers = {}
    for e, a in specials.items():
        sharers.setdefault(a, []).append(e)
    named = 0
    for a, es in sorted(sharers.items()):
        names = {bn[e] for e in es}
        key = name_key.get(a)
        if len(names) != 1 or not key or key not in game_text:
            continue
        b = names.pop()
        if b and (rename_all or any(vn[e] != b for e in es)):
            messages[key] = b
            named += 1
    copies = sum(1 for aid, (t, _, _) in writes.items() if aid != t)
    log(f"monster special attacks: {len(specials)} monsters' specials found in the PR's data ({len(sharers)} abilities); "
        f"{len(writes) - copies} rebuilt from BNW's byte in place, {copies} copies for monsters a shared special no "
        f"longer fits ({', '.join(f'{aid}: {sorted(w)}' for aid, (t, _, w) in sorted(writes.items()) if aid != t)}), "
        f"{len(mults)} with a damage multiplier (plugin), {len(groups.changed)} new function groups, {named} names; "
        f"no special in the PR's data: {sorted(set(range(384)) - set(specials))}")
    return {"ability": changed, "function_group": groups.changed, "content": new_content}
