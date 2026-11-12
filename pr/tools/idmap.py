"""SNES (US v1.0) id -> Pixel Remaster id mappings, checked by tools/check_vanilla.py."""
import prdata as P


def item(snes_id):
    """SNES item id -> (PR table, PR id). None for 'empty' (0xFF)."""
    i = snes_id
    if i <= 89:
        return "weapon", i + 2
    if 90 <= i <= 131:          # shields, helmets
        return "armor", i - 85
    if 132 <= i <= 162:         # body armor (PR armor id 47 is unused)
        return "armor", i - 84
    if 163 <= i <= 170:         # Edgar's tools
        return "item", i - 133
    if 171 <= i <= 175:         # skeans / scrolls
        return "item", i - 146
    if 176 <= i <= 230:         # relics
        return "armor", i - 97
    if i == 231:                # Rename Card
        return "item", 24
    if 232 <= i <= 254:         # consumables
        return "item", i - 231
    return None


def character(snes_id):
    return snes_id + 1


_content = None


def content_id(table, pr_id):
    """PR content.csv id for an item/weapon/armor (used by shops, drops, chests)."""
    global _content
    if _content is None:
        types = {"item": "1", "weapon": "2", "armor": "3"}
        rev = {}
        for r in P.Table("content"):
            rev[(r["type_id"], int(r["type_value"]))] = int(r["id"])
        _content = (types, rev)
    types, rev = _content
    return rev[(types[table], pr_id)]


def item_content(snes_id):
    m = item(snes_id)
    return content_id(*m) if m else 0


# SNES esper order (spell ids 54-80) -> PR summon ability ids
_ESPERS = [88, 92, 93, 90, 107, 96, 95, 99, 91, 103, 106, 111, 115, 112, 109, 114, 113,
           89, 101, 98, 97, 102, 100, 94, 105, 108, 110]
# SNES esper index (0-26, same order) -> PR summon number (kept for reference; use esper_content)
ESPER_ORDER = [x - 87 for x in _ESPERS]
_esper_content = None


def esper_content(snes_esper):
    """SNES esper index -> PR content.csv id of that esper (type 9). The summon
    ability and the esper entry share a name key, which is how they're linked."""
    global _esper_content
    if _esper_content is None:
        content = P.Table("content")
        ability_key = {int(r["type_value"]): r["mes_id_name"] for r in content if r["type_id"] == "4"}
        esper_by_key = {r["mes_id_name"]: int(r["id"]) for r in content if r["type_id"] == "9"}
        _esper_content = [esper_by_key.get(ability_key.get(a)) for a in _ESPERS]
    return _esper_content[snes_esper]


# A mod's spells in slots the PR keeps for something else (port.py sets it from the profile's
# spell_relocations; empty for vanilla and WC). BNW moved its Rage attacks into the MagiTek slots
# 131-137: they get abilities of their own, and the PR's Magitek moves (218-225) stay.
SPELL_OVERRIDES = {}


def spell(snes_id):
    """SNES spell/skill id (0-255) -> PR ability id, or None if the PR has no counterpart."""
    if snes_id in SPELL_OVERRIDES:
        return SPELL_OVERRIDES[snes_id]
    i = snes_id
    if i <= 44:
        return i + 40                 # black (0-23) + gray (24-44) magic
    if i <= 53:
        return i - 14                 # white magic
    if i <= 80:
        return _ESPERS[i - 54]
    if i <= 84:
        return i + 115                # skeans / ninja scrolls -> 196-199
    if i <= 92:
        return i + 39                 # Bushido -> 124-131
    if i <= 100:
        return i + 39                 # Blitz -> 132-139
    if i <= 124:
        return i + 71                 # Dance moves -> 172-195
    if 127 <= i <= 130:
        return i + 84                 # Slot results -> 211-214
    if i <= 138 and i >= 131:
        return i + 87                 # Magitek -> 218-225
    if i <= 162 and i >= 139:
        return i + 1                  # Lores -> 140-163
    if 163 <= i <= 237:
        return i + 137                # enemy skills -> 300-374
    if 240 <= i <= 251:
        return i - 12                 # desperation attacks -> 228-239
    if i == 252:
        return 226                    # Takedown
    if i == 253:
        return 227                    # Wild Fang
    if i == 254:
        return 210                    # Lagomorph -> Mysidian Rabbit
    return None


_parties = None


def party(snes_formation):
    """SNES formation id -> the vanilla PR monster party that fights it.

    Mostly formation + 1, but the PR renumbered or rebuilt ~120 formations (most of the last 90,
    many bosses): their line-up sits at another party, sometimes with the PR's own copies of a
    monster (same name, another id). Matched by line-up (monster ids or names); a party the
    PR's event scripts start wins, then formation + 1. Unmatched: formation + 1."""
    global _parties
    if _parties is None:
        _parties = _formation_parties()
    return _parties.get(snes_formation, snes_formation + 1)


PARTY_FILE = "formation_party.json"   # precomputed for the in-game generator (tools/wc_bundle.py)


def _formation_parties():
    import glob
    import json
    import os
    from collections import Counter, defaultdict

    shipped = os.path.join(os.path.dirname(os.path.abspath(__file__)), PARTY_FILE)
    if os.path.exists(shipped):
        return {int(k): v for k, v in json.load(open(shipped, encoding="utf-8")).items()}

    import ff6snes as S
    import port_encounters as E
    van = S.Rom(P.VANILLA_ROM)
    mp = P.Table("monster_party")
    names = P.messages("system", "en")
    mname = {int(r["id"]): names.get(r["mes_id_name"], r["mes_id_name"]) for r in P.Table("monster")}
    by_ids, by_names = defaultdict(list), defaultdict(list)
    for pid in mp.rows:
        if pid >= 1000:
            continue
        mem = E.party_members(mp[pid])
        by_ids[mem].append(pid)
        by_names[tuple(sorted(mname.get(m, "") for m in mem))].append(pid)
    used = Counter()
    for f in glob.glob(os.path.join(P.VANILLA, "map_*", "**", "s*.json"), recursive=True):
        try:
            sc = json.load(open(f, encoding="utf-8"))
        except (ValueError, OSError):
            continue
        for m in sc.get("Mnemonics", []) if isinstance(sc, dict) else []:
            if m.get("mnemonic") in ("Encount", "EncountBoss"):
                for v in m["operands"]["iValues"][:4 if m["mnemonic"] == "Encount" else 1]:
                    if v:
                        used[v] += 1
    out = {}
    for f in range(576):
        mem = tuple(sorted(e + 1 for e, _, _ in E.formation(van, f)))
        if not mem:
            continue
        if f + 1 in mp.rows and E.party_members(mp[f + 1]) == mem:
            out[f] = f + 1
            continue
        cands = set(by_ids.get(mem, [])) | set(by_names.get(tuple(sorted(mname.get(m, "") for m in mem)), []))
        if cands:
            # the one an event script starts (the White Dragon is 482, its line-up's twin 521 unused)
            out[f] = min(cands, key=lambda p: (used[p] == 0, p != f + 1, p))
    return out


def party_formation():
    """{PR party: the SNES formation it stands for} (party()'s reverse; formation + 1 first when
    two formations share a party: SNES duplicates like the dragons' twins at 517 / 554)."""
    out = {}
    for f in range(576):
        p = party(f)
        if p not in out or p == f + 1:
            out[p] = f
    return out
