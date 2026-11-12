"""Export Brave New World's esper rules (from the built BNW ROM) for the PR plugin.

Everything is converted to PR terms here so the plugin needs no SNES knowledge:
characters are PR character_status ids, espers are PR content ids, stats use the
PR parameter names, elements are PR attribute ids and statuses PR condition ids.

  plugin_data(rom) -> dict written to bnw_data.json
"""
import ff6snes as S
import idmap as M
import port_resist as R
import port_spells

EP_CHART = 0x2D8BCA      # ED/8BCA: 25 words, total EP needed for EL 1..25
EL_TABLE = 0x02614E      # C2/614E: 2 bytes per esper bonus
EQUIP_BONUS = 0x00D690   # C0/D690: 10 bytes per esper (applied by EsperBonuses at C2/0EF3)
ESPER_USERS = 0x03F0F5   # C3/F0F5: 4-byte esper bitmask per character (EsperData in c3.asm)
ESPER_DATA_BONUS = 10    # esper data byte 10 = EL bonus index

MAX_EL, MAX_EP, MAX_SP_BANK, STAT_CAP = 25, 50000, 30, 128
EL_STATS = ["power", "agility", "vitality", "magic"]  # SNES Vigor, Speed, Stamina, Magic


def el_bonus(pair):
    """Two ELTable bytes -> {"hp", "mp", "power", "agility", "vitality", "magic"} added per EL spent.
    Byte: 0 = none; bit0 set = +1 to stat (byte >> 1); else HP (bit1 clear) or MP (bit1 set) += byte >> 2."""
    out = {k: 0 for k in ["hp", "mp"] + EL_STATS}
    for x in pair:
        if x == 0:
            continue
        if x & 1:
            out[EL_STATS[(x >> 1) & 3]] += 1
        else:
            out["mp" if x & 2 else "hp"] += x >> 2
    return out


def equip_bonus(raw):
    """10 EsperBonuses bytes -> PR-side equip bonus."""
    protect = raw[0] | raw[1] << 8
    pct = raw[3]
    return {
        "power": raw[5] & 15, "agility": raw[5] >> 4, "vitality": raw[6] & 15, "magic": raw[6] >> 4,
        "defense": raw[7], "ability_defense": raw[8],
        "evasion_rate": raw[9] & 15, "ability_evasion_rate": raw[9] >> 4,
        "half_attributes": sorted(R.ELEM_ATTR[b] for b in range(8) if raw[4] >> b & 1),
        "protect_conditions": sorted({c for b in range(16) if protect >> b & 1 for c in R.PROTECT.get(b, [])}),
        "auto_conditions": sorted({c for b in range(8) if raw[2] >> b & 1 for c in R.AUTO_STATUS.get(b, [])}),
        "physical_pct": 25 if pct & 0x01 else 0, "magic_pct": 25 if pct & 0x02 else 0,
        "max_hp_pct": (25 if pct & 0x04 else 0) + (50 if pct & 0x08 else 0) + (12 if pct & 0x10 else 0),
        "max_mp_pct": (25 if pct & 0x20 else 0) + (50 if pct & 0x40 else 0) + (12 if pct & 0x80 else 0),
    }


def plugin_data(rom):
    d = rom.data
    espers = port_spells.esper_rules(rom)
    out = []
    for e in espers:
        i = e["snes_id"]
        users = d[ESPER_USERS:ESPER_USERS + 64]
        e = dict(e)
        e["content_id"] = M.esper_content(i)
        e["users"] = [c for c in range(16) if users[4 * c + (i >> 3)] >> (i & 7) & 1]
        e["user_status_ids"] = [M.character(c) for c in e["users"]]
        e["equip_bonus"] = equip_bonus(d[EQUIP_BONUS + 10 * i:EQUIP_BONUS + 10 * i + 10])
        out.append(e)
    n_bonus = max(e["el_bonus"] for e in out) + 1
    import ff6snes as S
    import port
    van = S.Rom(port.VANILLA_ROM)
    lists, passives = hard_items(van, rom)
    return {
        "procs": proc_rules(rom),
        "command_waits": command_waits(van, rom),
        "slot": slot_rules(van, rom),
        # brushes (BNW "Brushless Sketch", c2.asm BrushHand: the item name's first character is
        # the brush icon $E2): Sketch needs one in a hand; weapon content ids
        "sketch": {"command_id": PR_COMMAND_OF_SNES[SNES_SKETCH],
                   "brushes": [content for it, k, pid in _equipment(rom) if k == "weapon"
                               and rom.b(ITEM_NAMES + 13 * it.id, 1)[0] == BRUSH_ICON
                               for content in [_content_ids().get((k, pid), 0)]]},
        # Umaro's attack odds (C2/5269: rows none / Rage Ring / Blizzard Orb / both, columns
        # Fight, Tackle, Snowstorm, ally toss; weights of 255, $FF = not available)
        "umaro": {"columns": [1, 201, 199, 200],
                  "rows": [list(rom.b(0x025269 + 4 * r, 4)) for r in range(4)],
                  "vanilla_rows": [list(van.b(0x025269 + 4 * r, 4)) for r in range(4)]},
        # weapons that allow Runic / Bushido (SNES weapon byte 19 bit 7 / bit 1; the PR's
        # hard-coded BlockMagicSwordData / KillingSwordData .ValidWeaponContentId are exactly
        # vanilla's): weapon content ids
        "command_weapons": {cmd: {k: [x["content_id"] for x in _select(_equipment(r), ("bit", 19, bit), _content_ids())]
                                  for k, r in (("vanilla", van), ("bnw", rom))}
                            for cmd, bit in (("runic", 7), ("bushido", 1))},
        "spirit": spirit_abilities(rom),
        "hard_items": lists,
        "passives": passives,
        # base Battle Power per character (SNES init byte 10, ED/7CAA): Bushido's BP adds it
        "base_bp": {str(M.character(c.id)): c.attack for c in S.characters(rom)[:14] if M.character(c.id)},
        "rules": {"max_el": MAX_EL, "max_ep": MAX_EP, "max_sp_bank": MAX_SP_BANK, "stat_cap": STAT_CAP,
                  "max_el_character_status_id": 12,  # SNES: Gogo (12) and up never gain EL
                  "ep_chart": [int.from_bytes(d[EP_CHART + 2 * k:EP_CHART + 2 * k + 2], "little") for k in range(MAX_EL)]},
        "el_bonuses": [el_bonus(d[EL_TABLE + 2 * k:EL_TABLE + 2 * k + 2]) for k in range(n_bonus)],
        "espers": out,
    }


BONUS_KEY = "MSG_BNW_ESPER_BONUS_{:02d}"  # the plugin returns these from MagicStoneData.GetMagicBonusMessageId


def bonus_line(el, equip_text):
    """Same format as EsperRules.BonusLine in the plugin: 'EL: +2 Vigor   Equip: Halves Bolt damage'."""
    names = [("hp", "HP"), ("mp", "MP"), ("power", "Vigor"), ("agility", "Speed"), ("vitality", "Spirit"), ("magic", "Magic")]
    parts = [f"+{el[k]} {label}" for k, label in names if el.get(k)]
    line = f"EL: {'/'.join(parts)}" if parts else ""
    equip = " ".join(equip_text.split())
    if equip.startswith("Equip "):
        equip = equip[6:]
    if equip:
        line += ("   " if line else "") + "Equip: " + equip
    return line


# labels for the esper screen's button bar (EsperMenu relabels the Confirm button)
KEY_MESSAGES = {"MSG_BNW_KEY_LEARN": "Learn", "MSG_BNW_KEY_SPEND_EL": "Spend EL"}


def messages(data):
    out = dict(KEY_MESSAGES)
    for e in data["espers"]:
        line = bonus_line(data["el_bonuses"][e["el_bonus"]], e["equip_bonus_text"])
        if line:
            out[BONUS_KEY.format(e["snes_id"])] = line
    return out


# --- equipment the PR keys on item ids inside its code ------------------------------
# The PR hard-codes some effects to item ids (CalcExecuteCorrection lists, SpcialFlagData
# ids...). BNW moved those effects to other items, so the plugin rewrites each list:
# "vanilla" = what it should hold now (to recognise it and its id space), "bnw" = the
# items BNW gives that effect. Relic bits: (byte, bit); weapon specials: byte 27 nibble.
HARD_ITEMS = [
    ("Serial.FF6.Management.SpcialFlagData", "EncountDownItemId", "id", ("bit", 5, 0), ("bit", 5, 0)),
    ("Serial.FF6.Management.SpcialFlagData", "NonEncountItemId", "id", ("bit", 5, 1), ("bit", 5, 1)),
    ("Serial.FF6.Management.SpcialFlagData", "HealingItemId", "id", ("bit", 5, 7), ("bit", 5, 7)),
    ("Last.Map.Tamanosuzu", "HealingItemId", "id", ("bit", 5, 7), ("bit", 5, 7)),
    ("Serial.FF6.Management.SerialBattleUtility", "ArmorIdHornOfDragon", "id", ("bit", 10, 7), ("bit", 10, 7)),
    ("Serial.FF6.Management.ParameterProvider", "RingsOfTheDeadId", "id", ("bit", 13, 7), ("bit", 13, 7)),
    ("Serial.FF6.Management.PassiveContentProvider", "ContentIdGaleHairpin", "id", ("bit", 10, 0), ("bit", 10, 0)),
    ("Serial.FF6.Management.PassiveContentProvider", "ContentIdAlarmPiercing", "id", ("bit", 10, 1), ("bit", 10, 1)),
    ("Last.Battle.CalcExecuteCorrection", "PhysicalCorrectionAccessoryIds", "dict", ("bit", 9, 0), ("bit", 9, 0)),
    ("Last.Battle.CalcExecuteCorrection", "MagicCorrectionAccessoryIds", "dict", ("bit", 9, 1), ("bit", 9, 1)),
    # vanilla's list also has Ogre Nix (E: may break + MP crit); BNW's E is Aero
    ("Last.Battle.CalcExecuteCorrection", "UseMpCriticalWeaponIds", "array", ("special", 7, 14), ("special", 7)),
    ("Last.Battle.CalcExecuteCorrection", "NormalAttackHeal", "array", ("special", 12), ("special", 12)),
    # BNW: 5 = ignore Defense (+ x2 vs humans), no more drain; E = Aero, no more breaking
    ("Last.Battle.CalcExecuteCorrection", "IgnoreDefenseItemIds", "array", ("special", 12), ("special", 5, 12)),
    ("Last.Battle.CalcExecuteCorrection", "bloodSwordId", "id", ("special", 5), ("none",)),
    ("Serial.FF6.Battle.BattleActExectionSerial", "UseBrokenWeaponIds", "array", ("special", 14), ("none",)),
]
# x2 EXP (Exp. Egg) and x2 gil (Cat Hood): BNW removed both (c2.asm 4561)
PASSIVE_BITS = {"exp_x2": (13, 3), "gil_x2": (13, 4)}


def _content_ids():
    import prdata as P
    out = {}
    for r in P.Table("content"):
        if r["type_id"] in ("2", "3"):
            out[("weapon" if r["type_id"] == "2" else "armor", int(r["type_value"]))] = int(r["id"])
    return out


def _equipment(rom):
    """(item, PR kind, PR id) for every SNES weapon/armor slot of a ROM."""
    import ff6snes as S
    out = []
    for it in S.items(rom):
        m = M.item(it.id)
        if m and m[0] in ("weapon", "armor") and 1 <= (it.raw[0] & 7) <= 5:  # still equipment in this ROM
            out.append((it, m[0], m[1]))
    return out


def _select(equipment, rule, content):
    kind = rule[0]
    picked = []
    for it, k, pid in equipment:
        raw = it.raw
        if kind == "bit" and raw[rule[1]] >> rule[2] & 1:
            picked.append((it, k, pid))
        elif kind == "special" and k == "weapon" and (raw[27] >> 4) in rule[1:]:
            picked.append((it, k, pid))
    return [{"item_id": pid, "content_id": content.get((k, pid), 0), "name": it.name.strip()} for it, k, pid in picked]


def hard_items(van, bnw):
    content = _content_ids()
    ve, be = _equipment(van), _equipment(bnw)
    lists = []
    for cls, member, kind, vrule, brule in HARD_ITEMS:
        lists.append({"type": cls, "member": member, "kind": kind,
                      "vanilla": _select(ve, vrule, content), "bnw": _select(be, brule, content)})
    passives = {k: _select(be, ("bit",) + bit, content) for k, bit in PASSIVE_BITS.items()}
    return lists, passives


def proc_rules(bnw):
    """Weapon proc chances per weapon (port_procs) and the relics with High Proc Rate."""
    import port_procs
    import profiles
    odds = profiles.PROFILES["bnw"].proc_odds
    content = _content_ids()
    high = [content.get((k, pid), 0) for it, k, pid in _equipment(bnw)
            if k == "armor" and it.raw[odds["high"][0]] >> odds["high"][1] & 1]
    rates = port_procs.LAST.get("rates", {})
    return {"base_rate": 25, "high_rate": odds["high_rate"],
            "weapons": [{"weapon_id": w, "rate": r[0], "high_rate": r[1]} for w, r in sorted(rates.items())],
            "high_rate_content_ids": sorted(c for c in high if c)}


# --- command wait times (the Wait Gauge's "cast bar") --------------------------------
CMD_WAITS = 0x02067B  # C2/067B CommandWaitTimes, 1 byte per SNES command
# SNES command -> PR command id (Morph = Trance 8, Capture = Mug 11, GP Rain = Gil Toss 21,
# Health = Pray 27); SNES 25 Summon is magic in the PR: summon abilities are type 9
PR_COMMAND_OF_SNES = {0: 1, 1: 3, 2: 7, 3: 8, 4: 9, 5: 10, 6: 11, 7: 12, 8: 13, 9: 14, 10: 15, 11: 16, 12: 17,
                      13: 18, 14: 19, 15: 20, 16: 23, 17: 24, 18: 25, 19: 22, 20: 5, 21: 2, 22: 30, 23: 31,
                      24: 21, 26: 27, 27: 28, 28: 29, 29: 26}
SNES_SUMMON, PR_SUMMON_TYPE = 25, 9


def command_waits(van, bnw):
    """The PR scales its own chant times by BNW's wait over vanilla's, per command."""
    v, b = van.b(CMD_WAITS, 30), bnw.b(CMD_WAITS, 30)
    return {"commands": [{"command_id": pr, "vanilla": v[sn], "bnw": b[sn]} for sn, pr in sorted(PR_COMMAND_OF_SNES.items())],
            "summon": {"ability_type": PR_SUMMON_TYPE, "vanilla": v[SNES_SUMMON], "bnw": b[SNES_SUMMON]},
            "fight_command_id": 1}


ITEM_NAMES, BRUSH_ICON = 0x12B300, 0xE2  # D2/B300: 13-byte item names; icon of BNW's brushes
SNES_SKETCH = 13


# --- Setzer's Slot: reel layout and rigging (plugin SlotRules) ----------------------------
SLOT_REELS = 0x02A800        # C2/A800: 3 reels x 16 symbols x 2 bytes (synchysi slots.asm)
SLOT_RIG = (0x01806D, 0x0180A6)  # C1/806D, C1/80A6: reel 2/3 rigging; BNW branches over both
# SNES symbol -> PR SlotDesignPattern (Diamond 0, Chocobo 1, BAR 2, Airship 3, Bahamut 4, Seven 5);
# checked: vanilla's reels map onto the PR's own lists in the same order
SLOT_SYMBOL = {0: 5, 1: 4, 2: 2, 3: 3, 4: 1, 5: 0}


def slot_rules(van, bnw):
    def reels(rom):
        raw = rom.b(SLOT_REELS, 96)
        return [[SLOT_SYMBOL[raw[32 * r + 2 * i]] for i in range(16)] for r in range(3)]
    derigged = all(bnw.b(a, 1)[0] == 0x80 and van.b(a, 1)[0] != 0x80 for a in SLOT_RIG)
    return {"reels": reels(bnw), "vanilla_reels": reels(van), "derigged": derigged}


# --- Spirit (SNES Stamina) in place of Magic --------------------------------------------
def spirit_abilities(bnw):
    """Abilities that use Spirit instead of Magic (spell byte 7 bit 2, c2.asm:1448 StamTest)
    and whose status Spirit can evade (byte 4 bit 4, c0.asm:436), as PR ability ids, plus
    their proc copies; monster special attack damage multipliers (port_specials)."""
    import ff6snes as S
    import port_procs
    import port_specials
    rows = bnw.table(*S.SPELLS)
    copies = port_procs.LAST.get("copies", {})

    def ids(test):
        out = set()
        for i, d in enumerate(rows):
            if test(d) and M.spell(i) is not None:
                out.add(M.spell(i))
                if M.spell(i) in copies:
                    out.add(copies[M.spell(i)])
        return sorted(out)

    import ff6snes as S
    # monster special attacks whose status Spirit can evade: every special with a status
    # part (0x00-0x1F) unless its "can't dodge" bit 7 is set (C2/334B -> C2/6761)
    import prdata as P
    van = S.Rom(P.VANILLA_ROM)
    special_evade = sorted({port_specials.special_of(van, bnw, e.id) for e in S.enemies(bnw)
                            if (e.raw[31] & 0x3F) < 0x20 and not e.raw[31] & 0x80} - {None})
    return {"magic_abilities": ids(lambda d: d[7] & 0x04),
            "evade_abilities": ids(lambda d: d[4] & 0x10),
            # "physical" spells (byte 2 bit 0): Defense instead of MDef, Vigor for the variance
            "physical_abilities": ids(lambda d: d[2] & 0x01),
            # BNW's "Respect Weapon Flags" (byte 7 bit 3; c2.asm Wpn_Chk: the Windslash / Aero
            # procs): x1.5 from a two-handed grip, else x0.75
            "weapon_flag_abilities": ids(lambda d: d[7] & 0x08),
            "special_evade_abilities": special_evade,
            "special_multipliers": {str(k): v for k, v in sorted(port_specials.LAST.get("multipliers", {}).items())}}
