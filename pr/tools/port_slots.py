"""Setzer's Slot results the mod changed (profile.slot_jackpot).

The PR's slot results are fixed in code (decompiled tools/decomp/slot,
Serial.FF6.Management.SlotInfomationData): three of a kind -> 213 Diamond, 211 Chocobo,
214 Bar (random esper), 212 Airship, 112 Bahamut (the esper), 216 Seven ("Joker's Death",
kills every foe); 7-7-Bar -> 215 (Joker's Death on the party); anything else -> 210
(Lagomorph / BNW's Go Fish). The 3-of-a-kind spells map from the SNES slots already
(idmap 127-130 -> 211-214), so the mod's Trifecta/Blackjack/Solitaire port with the spells.

BNW (c2.asm 1126, synchysi slots.asm, checked in the 3.0 ROM): 7-7-7 casts Jackpot (spell
151: heal power 10 on the party, lifts Death; "Cures HP/revives allies") instead of Joker
Doom, and 7-7-Bar can't come up (C2B4AF returns 7). Here:
  * 216 becomes Jackpot: party, standard value = the spell's power, functions = lift Death
    and revive to max HP / 8 (Life's two, function group 432) + heal with Cure's formula
    (function group 431's heal); Cure 3's animation; no-split/defense flags from Go Fish
    (BNW gives both the same spell flags). Name "Jackpot".
  * 215 becomes a copy of the losing spin (210), so a 7-7-Bar is a loss.
  * Bar-Bar-Bar (PR 214: function 46 RandomAbility over ability_random_group 9, 25 espers at 4,
    Odin/Raiden out, Bahamut as its own copy 1532): BNW rolls any of its 27 esper spells
    (C2/37DC "LDA #$1B; JSR random; CLC; ADC #$36"; vanilla rolls 25 and skips one). Group 9
    becomes BNW's 27 (weights 4 x 19 + 3 x 8 = 100, like the PR's percent groups), Bahamut still
    through the PR's copy, which takes BNW's ported Bahamut.
The reel layout and the de-rigging are code (plugin SlotRules.cs).
"""
import ff6snes as S
import idmap as M

SEVENS_FOES, SEVENS_PARTY, LOSING = 216, 215, 210
LIFE, CURE, CURE3 = 34, 31, 33
COPY_SKIP = {"id", "sort_id"}


def fsig(sp, gid):
    import port_spells
    return [list(f) for f in port_spells.fsig(sp.fg, gid)]


def port(van, bnw, tables, sp, log, profile=None):
    changed = {"ability": set()}
    jackpot = getattr(profile, "slot_jackpot", None)
    if jackpot is None:
        return changed
    ab, vab = sp.ab, sp.vab
    spell = {s.id: s for s in S.spells(bnw)}[jackpot]

    def setv(aid, col, val):
        row = ab.rows[aid]
        if row[col] != str(val):
            row[col] = str(val)
            changed["ability"].add(aid)

    # 7-7-7: Jackpot
    life = fsig(sp, int(vab[LIFE]["battle_function_group_id"]))     # lift Death, revive max HP / 8
    heal = [f for f in fsig(sp, int(vab[CURE]["battle_function_group_id"])) if f[0] == 1][:1]  # Cure's heal
    funcs = [f for f in life if f[0] in (12, 34)] + heal
    gid = sp.function_group(funcs)
    loser = ab.rows[LOSING]
    for col, val in (("battle_function_group_id", gid), ("standard_value", spell.power),
                     ("renge_id", loser["renge_id"]), ("battle_renge_id", loser["battle_renge_id"]),
                     ("menu_renge_id", loser["menu_renge_id"]), ("system_id", loser["system_id"]),
                     ("content_flag_group_id", loser["content_flag_group_id"]),
                     ("accuracy_rate", loser["accuracy_rate"]),
                     ("battle_effect_asset_id", vab[CURE3]["battle_effect_asset_id"])):
        setv(SEVENS_FOES, col, val)
    # 215 and 216 share one name key (MSG_MAGIC_NAME_144): Jackpot gets its own
    own, other = sp.content_by_ability.get(SEVENS_FOES), sp.content_by_ability.get(SEVENS_PARTY)
    if own and other and own["mes_id_name"] == other["mes_id_name"]:
        own["mes_id_name"] = f"MSG_BNW_MAGIC_NAME_{SEVENS_FOES}"
        sp.changed["content"].add(int(own["id"]))
    sp.set_text(SEVENS_FOES, SEVENS_FOES, spell.name, "Cures HP and revives allies")
    # 7-7-Bar: a losing spin
    for col, val in loser.items():
        if col not in COPY_SKIP:
            setv(SEVENS_PARTY, col, val)
    lose_name = {s.id: s.name for s in S.spells(bnw)}.get(254)  # SNES 254: Lagomorph / Go Fish
    sp.set_text(SEVENS_PARTY, LOSING, lose_name, None)
    log(f"slot: 7-7-7 -> {spell.name} (ability {SEVENS_FOES}: power {spell.power}, functions {funcs}); "
        f"7-7-Bar -> the losing spin (ability {SEVENS_PARTY} = {LOSING})")
    for name, ids in bar_espers(van, bnw, tables, sp, log).items():
        changed.setdefault(name, set()).update(ids)
    return changed


BAR_ROLL = 0x0237DC   # C2/37DC: Bar-Bar-Bar's esper roll
BAR_GROUP = 9         # ability_random_group of PR ability 214 (function 46)


def bar_espers(van, bnw, tables, sp, log):
    import prdata as P
    code = bnw.b(BAR_ROLL, 9)
    if not (code[0] == 0xA9 and code[2] == 0x20 and code[5:7] == b"\x18\x69" and code[8] == 0x60):
        return {}  # not BNW's plain "random(n) + base": leave the PR's list
    count, base = code[1], code[7]
    want = [M.spell(base + i) for i in range(count)]
    rg = tables.setdefault("ability_random_group", P.Table("ability_random_group"))
    rows = sorted((r for r in rg if r["group_id"] == str(BAR_GROUP)), key=lambda r: int(r["id"]))
    have = [int(r["ability_id"]) for r in rows]
    # the PR summons some espers through its own copies (Bahamut 112 -> 1532): keep those
    copies = {}
    for a in have:
        if a not in want and a in sp.vab.rows:
            twin = next((w for w in want if w in sp.vab.rows and w not in have and
                         all(sp.vab[w][c] == sp.vab[a][c] for c in ("type_id", "standard_value", "battle_function_group_id", "battle_effect_asset_id"))), None)
            if twin:
                copies[twin] = a
    want = [copies.get(w, w) for w in want]
    changed = {"ability_random_group": set(), "ability": set()}
    for twin, copy in copies.items():  # the copy takes BNW's ported esper
        for col, val in sp.ab.rows[twin].items():
            if col not in ("id", "sort_id") and sp.ab.rows[copy][col] != val:
                sp.ab.rows[copy][col] = val
                changed["ability"].add(copy)
    weights = [4] * count
    for i in range(count - 1, -1, -1):  # bring the total to 100
        if sum(weights) <= 100:
            break
        weights[i] = 3
    next_id = max(rg.rows) + 1
    for i, (aid, w) in enumerate(zip(want, weights)):
        if i < len(rows):
            row = rows[i]
        else:
            row = dict(rows[0], id=str(next_id))
            rg.rows[next_id] = row
            next_id += 1
        if row["ability_id"] != str(aid) or row["invocation_rate"] != str(w):
            row["ability_id"], row["invocation_rate"] = str(aid), str(w)
            changed["ability_random_group"].add(int(row["id"]))
    for row in rows[count:]:  # fewer than the PR's: drop the rest from the roll
        row["invocation_rate"] = "0"
        changed["ability_random_group"].add(int(row["id"]))
    log(f"slot: Bar-Bar-Bar -> {count} espers (was {len(have)}; {sum(weights)} total weight; "
        f"through the PR's copies {copies})")
    return changed
