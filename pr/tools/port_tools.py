"""Edgar's Tools: the mod's tool data (SNES items 0xA3-0xAA) onto the PR's tool items 30-37.

The item porter only moves prices for the PR's `item` table, so tool power, hit rate and
effects stayed vanilla while the names/descriptions were the mod's (BNW: "Defibrator - Revives
fallen ally" still did Debilitator's work). Per tool:
  * tools that cast a spell (SNES Tool_Data_1/2: tool id - offset = spell; BNW c2.asm 2767:
    Bio Blaster 125, Flash 126, Defibrillator 154, Mana Battery 155; profile.tool_spells):
    the PR item takes the ported spell ability's effect, power, element, hit and targets
    when the PR has an ability for that spell, else just the spell's power
  * other tools: power (byte 20) and hit rate (byte 21) when the mod changed them
  * BNW's extra effects (c2.asm 2016-2063): Drill also sets Sap
Already the game's: Chainsaw/Drill ignore Defense (CalcExecuteCorrection.IgnoreDefenseItemIds
= [35, 33]), Chainsaw's 25% kill (function 46). The plugin gives the physical tools BNW's Fight
formula (BattleFormulas). Not done: Auto Crossbow's no-split damage and Schematics upgrade
(BP 225, never misses), Noise Blaster's Stamina evasion.
"""
import ff6snes as S
import idmap as M

TOOLS = range(0xA3, 0xAB)
DRILL = 0xA8
SAP = 16          # PR condition for SNES status 14 (port_spells.STATUS_TO_COND)
MP_HEAL = [1, 2, 3, 0]  # function 1 = heal; value1 2 = spell formula (Cure's), value2 3 = MP


def port(van, bnw, tables, sp, log, profile=None):
    items, ab = tables["item"], sp.ab
    changed = {"item": set(), "ability": set()}
    spells = getattr(profile, "tool_spells", None) or {}
    vt, bt = {i.id: i for i in S.items(van)}, {i.id: i for i in S.items(bnw)}
    vsp = {s.id: s for s in S.spells(van)} if hasattr(S, "spells") else {}
    bsp = {s.id: s for s in S.spells(bnw)} if hasattr(S, "spells") else {}
    notes = []

    def setv(row, col, val, pid):
        if row[col] != str(val):
            row[col] = str(val)
            changed["item"].add(pid)

    # BNW's Mana Battery spell (no vanilla spell restores an ally's MP: the template would be
    # its slot's own, Dischord): an MP heal with the spell formula, one ally
    if spells and profile and getattr(profile, "mp_heal_spells", None):
        for s in profile.mp_heal_spells:
            k = M.spell(s)
            if k in ab.rows:
                row = ab.rows[k]
                gid = str(sp.function_group([MP_HEAL]))
                if row["battle_function_group_id"] != gid:
                    row["battle_function_group_id"] = gid
                    for col in ("battle_renge_id", "renge_id"):
                        row[col] = "1"  # one target, cursor on the party (Esuna, Regen use it)
                    changed["ability"].add(k)
                    notes.append(f"spell {s} -> MP heal (ability {k})")
    for snes in TOOLS:
        m = M.item(snes)
        if not m or m[0] != "item":
            continue
        pid = m[1]
        row = items[pid]
        v, b = vt[snes], bt[snes]
        if snes in spells:
            s = spells[snes]
            k = M.spell(s)
            if k in ab.rows:
                a = ab.rows[k]
                for col in ("standard_value", "attribute_id", "battle_function_group_id", "battle_renge_id", "accuracy_rate"):
                    setv(row, col, a[col], pid)
                notes.append(f"{b.name or v.name}: spell {s} (ability {k})")
            elif s in bsp and s in vsp and bsp[s].power != vsp[s].power:
                setv(row, "standard_value", bsp[s].power, pid)
                notes.append(f"{b.name or v.name}: spell {s} power {bsp[s].power}")
            continue
        if b.power != v.power and b.power:
            setv(row, "standard_value", b.power, pid)
        if b.hit_or_mdef != v.hit_or_mdef:
            setv(row, "accuracy_rate", b.hit_or_mdef, pid)
        if snes == DRILL and getattr(profile, "tool_rules", None) == "bnw":
            # BNW: Drill adds Sap (c2.asm Drill: attack status-2 bit 6)
            funcs = [list(f) for f in fsig(sp, int(row["battle_function_group_id"])) if f[0] != 9]
            funcs.append([9, SAP, 0, 0])
            setv(row, "battle_function_group_id", sp.function_group(funcs[:3]), pid)
            notes.append("Drill: + Sap")
    log(f"tools: {len(changed['item'])} tool items changed ({'; '.join(notes) or 'power/hit only'})")
    return changed


def fsig(sp, gid):
    import port_spells
    return port_spells.fsig(sp.fg, gid)
