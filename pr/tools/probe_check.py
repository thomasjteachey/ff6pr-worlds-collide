"""Check the in-game formula probe against FF6's and Brave New World's formulas.

The plugin's FormulaProbe (BepInEx/bnw_probe.request; headless launch, see PORTING.md) runs
every ability's damage/heal functions through the game's own calculation code over a grid
of attacker/target stats and writes BepInEx/bnw_probe.csv: per case the game's own value
(raw_*: before BNW's damage rules) and the final one. Two rule sets are checked:

  vanilla   SNES FF6's formulas against the game's own values (raw_*: a first pass with BNW's
            battle rules off): where the PR itself differs
            from the SNES game. The baseline for every port (Worlds Collide, Beyond Chaos...)
  bnw       Brave New World's formulas (c2.asm) against the final values: the BNW port

Per ability function and direction: pass (every sample in range), fail (the report gives
game / expected per case), no hits (misses, zero, errors), unchecked (no formula here for
that function type yet: the report says which stats move the result).

  .venv/Scripts/python tools/probe_check.py [probe.csv]    exit 1 when anything fails

Writes pr/probe_report.md. Formulas (SNES integer steps):
  Fight, character  vanilla  BP + (Lv*Lv*(BP + Vig2)/256)*3/2, Vig2 = min(2 Vigor, 255)
                    BNW      (BP/16)*(Lv*(Lv*Vig/16))/24 + BP + Vig2 (c2.asm:4893)
  Fight, monster    vanilla  Lv*Lv*(4 BP + Vigor 56..63)/256;  BNW: Vigor = 60 (c2.asm:2080)
  magic / heals     SP*4 + Lv*Mag*SP/32; vanilla monsters cast with Magic*3/2 (the probe shows
                    Magic, not spell power), BNW removed it (2169);
                    BNW: Spirit (Stamina) instead of Magic for its Spirit list
  specials          monster Fight x the special's multiplier (vanilla / BNW byte 31)
  variance          vanilla 224..255/256 + 1; BNW on characters ((255-S) - rand(0..30-S/4))/225,
                    S = Vigor (physical) or Stamina (magic) (c2.asm:5761); heals vanilla
  defense           (255 - Def)*d/256 + 1 (MDef for magic); damage capped at 9999
Which defense an ability meets (Def, MDef or none) is measured from the probe (Def/MDef 0 vs
255 cases) and compared with the ROM's spell flags (ignore defense, physical): differences
are listed apart from formula failures. Character Fight: the game caches Battle Power, so
the BP its formula used is fitted from the Level/Vigor cases.
"""
import csv
import json
import os
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
PR = os.path.dirname(HERE)
sys.path.insert(0, HERE)
import ff6snes as S  # noqa: E402
import idmap as M  # noqa: E402
import port_specials  # noqa: E402
import prdata as P  # noqa: E402
import profiles  # noqa: E402

GAME = r"D:\SteamLibrary\steamapps\common\FINAL FANTASY VI PR\BepInEx"
DEFAULT = os.path.join(GAME, "bnw_probe.csv")
REPORT = os.path.join(PR, "probe_report.md")
MOD = os.path.join(PR, "mods", "BraveNewWorld")

FN_NAMES = {1: "Recovery", 2: "Physical", 3: "Magic", 13: "Absorption", 23: "Unique", 32: "BaseValueFixedHPRecovery",
            33: "BaseValueFixedHPMagic", 34: "ResultValueFixedHPRecovery", 36: "OneDigitHP",
            41: "ParameterFixedCoefficientPhysical", 48: "DifferentRange", 50: "ThrowItem", 55: "SelectHp",
            56: "UserSelectHp", 65: "RecoveryEx", 68: "MagicAbsorption", 70: "Kill", 74: "MagicPointReduction",
            76: "PhysicsAbsorption", 77: "PowerDependentMagic"}


# --- BNW formulas (integer steps like the SNES) -------------------------------------------

def bnw_fight(lv, bp, vigor):
    vig2 = min(2 * vigor, 255)
    vig = vig2 >> 1
    t = lv * ((lv * vig) >> 4)
    if t > 0xFFFF:
        return 30000
    u = (bp >> 4) * t
    if u >> 16 >= 7:
        return 30000
    return min(30000, u // 24 + bp + vig2)


def bnw_shock(lv, sta, hp, back_row=False):
    """BNW Shock (c2.asm C23EDD) in 16 bits, as the plugin's BattleMath.Shock."""
    x = (lv * sta + hp) & 0xFFFF
    twice = x << 1
    v = ((twice & 0xFFFF) + x + (1 if twice > 0xFFFF else 0)) & 0xFFFF
    v >>= 2
    return v >> 1 if back_row else v


SHOCK_TYPE = 618        # the Shock command's ability (PR 19)
WEAPON_FLAGS = set()    # BNW's weapon-flag spells (bnw_data.json): x0.75 unless gripped (main sets it)


def bnw_monster_fight(lv, bp):
    return lv * (4 * bp + 60) * lv // 256


def vanilla_fight(lv, bp, vigor):
    vig2 = min(2 * vigor, 255)
    return bp + (lv * lv * (bp + vig2) // 256) * 3 // 2


def vanilla_monster_fight(lv, bp):
    return lv * lv * (4 * bp + 56) // 256, lv * lv * (4 * bp + 63) // 256


def magic(lv, mag, sp):
    return int(sp * 4 + lv * mag * sp / 32) if isinstance(mag, float) else sp * 4 + lv * mag * sp // 32


def vanilla_variance(d):
    return d * 224 // 256 + 1, d * 255 // 256 + 1


def defensive_variance(d, s):
    rng = 30 - s // 4
    hi = 255 - s if rng >= 0 else (255 - s) - rng
    lo = (255 - s) - rng if rng >= 0 else hi
    return d * lo // 225, d * hi // 225


def defense(d, df):
    return (255 - df) * d // 256 + 1


def apply(rng, f):
    return f(rng[0]), f(rng[1])


MAX_HIT = 9999  # game_constant_int MAX_HIT_VALUE


def capped(rng):
    return min(MAX_HIT, rng[0]), min(MAX_HIT, rng[1])


# --- data ---------------------------------------------------------------------------------

def special_multipliers():
    """PR ability id -> BNW multiplier (and vanilla's) of each monster special attack."""
    van = S.Rom(os.path.join(PR, "..", "rom", "vanilla", "Final Fantasy III (USA).sfc"))
    bnw = S.Rom(profiles.BraveNewWorld.rom)
    out = {}
    for v, b in zip(S.enemies(van), S.enemies(bnw)):
        aid = port_specials.special_of(van, bnw, b.id)   # the monster's own special (not 377 + id)
        if aid is not None:
            out[aid] = (port_specials.multiplier(b.raw[31]), port_specials.multiplier(v.raw[31]),
                        bool(b.raw[31] & 0x40), bool(v.raw[31] & 0x40))
    return out


def spell_flags(rom_path):
    """PR ability id -> (ignores defense, physical) from a ROM's spell data byte 2 (bits 5, 0)."""
    out = {}
    for s in S.spells(S.Rom(rom_path)):
        aid = M.spell(s.id)
        if aid is not None:
            out.setdefault(aid, (bool(s.flags1 & 0x20), bool(s.flags1 & 0x01)))
    return out


def names():
    msg = P.messages()
    path = os.path.join(MOD, "message", "Assets", "GameAssets", "Serial", "Data", "Message", "system_en.txt")
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            msg.update(dict(l.rstrip("\r\n").split("\t", 1) for l in f if "\t" in l))
    content = P.Table("content")
    cpath = os.path.join(MOD, "master", "Assets", "GameAssets", "Serial", "Data", "Master", "content.csv")
    if os.path.exists(cpath):
        for r in csv.DictReader(open(cpath, encoding="utf-8")):
            content.rows[int(r["id"])] = r
    return {int(r["type_value"]): P.strip_tags(msg.get(r["mes_id_name"], "")) for r in content if r["type_id"] == "4"}


def read(path):
    header, rows = [], []
    with open(path, encoding="utf-8") as f:
        lines = [l for l in f]
    header = [l[2:].strip() for l in lines if l.startswith("# ")]
    for r in csv.DictReader(l for l in lines if not l.startswith("#")):
        for k, v in r.items():
            if v not in (None, "") and k not in ("dir", "case", "a_row", "t_row"):
                try:
                    r[k] = float(v) if "." in v else int(v)
                except ValueError:
                    pass
        rows.append(r)
    return header, rows


# --- expectations -------------------------------------------------------------------------

PR_SPREAD = (224 / 256 / 0.9355, 255 / 256 / 0.9355)  # the game's roll over its mean, left in by the plugin
BASE_BP = 100  # the game caches a character's Battle Power: only the base value reaches its Fight formula


def expected(r, rules, spirit, specials, flags=None):
    """(lo, hi) for a row's hits under "vanilla" or "bnw" rules, or (None, kind).

    BNW on characters: the plugin scales the game's value, so the game's own roll stays in
    (PR_SPREAD) and monster Fight keeps vanilla's random Vigor 56..63; BNW's mean is kept."""
    fn, pe, atype, aid = r["fn_type"], r["dir"] == "PE", r["ability_type"], r["ability"]
    lv, vig, sta, mag, atk = r["a_lv"], r["a_vig"], r["a_sta"], r["a_mag"], r["a_atk"]
    tvig, tsta, tdef, tmdef = r["t_vig"], r["t_sta"], r["t_def"], r["t_mdef"]
    sp = r["standard"]
    bnw = rules == "bnw"
    tvig_snes = min(2 * tvig, 255) // 2

    def on_character(base_lo, base_hi, s):
        if bnw:
            return (int(defensive_variance(base_lo, s)[0] * PR_SPREAD[0]),
                    int(defensive_variance(base_hi, s)[1] * PR_SPREAD[1]) + 1)
        return vanilla_variance(base_lo)[0], vanilla_variance(base_hi)[1]

    if fn == 2 and atype == 4:  # Fight
        if pe:
            bp = atk  # probe runs from 2026-11-05 late set the per-hand BP (GetArmConfirmedAttack) too
            rng = apply(vanilla_variance(vanilla_fight(lv, bp, vig)), lambda d: defense(d, tdef))
            if bnw:  # the plugin scales the game's value by BNW / vanilla for the BP it reads
                ratio = bnw_fight(lv, atk, vig) / max(1, vanilla_fight(lv, atk, vig))
                rng = (int(rng[0] * ratio), int(rng[1] * ratio) + 1)
            return rng, "Fight"
        else:
            lo, hi = vanilla_monster_fight(lv, atk)
            rng = on_character(lo, hi, tvig_snes)
        return apply(rng, lambda d: defense(d, tdef)), "Fight"
    if fn == 2 and atype == 602 and sp > 0:  # Blitz (physical skill): spell power as Battle Power
        if not pe:
            return None, "physical skill (monsters don't cast Blitzes)"
        if pe:
            rng = vanilla_variance(bnw_fight(lv, sp, vig) if bnw else vanilla_fight(lv, sp, vig))
        else:
            lo, hi = vanilla_monster_fight(lv, sp)
            rng = on_character(lo, hi, tvig_snes)
        mode = (flags or {}).get((aid, r["fn"], r["dir"], rules), "def")
        if mode == "none":
            return rng, "physical skill ign. def"
        return apply(rng, lambda d: defense(d, tdef)), "physical skill"
    if fn == 23 and aid in specials and not pe:  # monster special attack
        bnw_m, van_m, bnw_none, van_none = specials[aid]
        m, none = (bnw_m, bnw_none) if bnw else (van_m, van_none)
        if none:
            return None, "special without damage"
        lo, hi = vanilla_monster_fight(lv, atk)
        rng = on_character(int(lo * m), int(hi * m), tvig_snes)
        return apply(rng, lambda d: defense(d, tdef)), f"special x{m:g}"
    if bnw and pe and atype == SHOCK_TYPE and fn == 3 and sp > 0:
        # the plugin scales the game's magic hit (defense ignored: the port's content flags) by
        # BNW's Shock over the game's magic formula; the probe keeps every unit in the front row
        return capped(vanilla_variance(bnw_shock(lv, sta, r["a_hp"]))), "Shock"
    use_spirit = bnw and aid in spirit
    # BNW monster Stamina (c2.asm:2160): max HP high byte + 1, at most 32 (probe max HP 5000)
    m = (sta if pe else min((5000 >> 8) + 1, 32)) if use_spirit else mag
    mode = (flags or {}).get((aid, r["fn"], r["dir"], rules), "?")  # measured: def / mdef / none / ?
    if mode == "?":
        ign, phys = (flags or {}).get(("rom", rules, aid), (False, False))
        mode = "none" if ign else "def" if phys else "mdef"
    ignore_def, physical = mode == "none", mode == "def"
    m = m if pe or bnw else m * 1.5  # vanilla monsters: Magic x1.5, as a fraction (Magic 1 hits like 1.5)
    tag = " (Spirit)" if use_spirit else ""
    if fn == 3 and sp > 0:  # magic damage
        base = magic(lv, m, sp)
        s_def = tvig_snes if physical else tsta
        rng = vanilla_variance(base) if pe else on_character(base, base, s_def)
        if bnw and pe and aid in WEAPON_FLAGS:
            # Wpn_Chk: x0.75 unless the caster grips its weapon (the probe's character doesn't)
            rng = (int(rng[0] * 0.75), int(rng[1] * 0.75) + 1)
            tag += " x0.75 (weapon flags)"
        tag += " ign. def" if ignore_def else " phys." if physical else ""
        if ignore_def:
            return rng, "magic" + tag
        return apply(rng, lambda d: defense(d, tdef if physical else tmdef)), "magic" + tag
    if fn == 1 and sp > 0:  # heal
        return vanilla_variance(magic(lv, m, sp)), "heal" + tag
    return None, FN_NAMES.get(fn, f"function {fn}")


def observed(r, heal, raw):
    """(min, max) of the row's hits (heals for heal functions): the final values, or with raw
    the game's own values before BNW's rules. Older probe files have no raw columns: then raw
    comes from a constant factor, or is unknown (None)."""
    n = r["heal_n"] if heal else r["hit_n"]
    if not n:
        return None
    final = (r["heal_min"], r["heal_max"]) if heal else (r["hit_min"], r["hit_max"])
    if not raw:
        return final
    if "raw_min" in r:
        return (r["raw_min"], r["raw_max"]) if r["raw_min"] not in (None, "") else final
    f0, f1 = r.get("factor_min"), r.get("factor_max")
    if f0 in (None, ""):
        if heal and r["dir"] == "EP":
            return None  # older run: monster heals were scaled without a recorded factor
        return final
    if f0 == f1:
        return round(final[0] / f0), round(final[1] / f0)
    return None


# --- one-factor sensitivity (for unchecked types) -----------------------------------------

FACTORS = [("a_Level", "a_lv"), ("a_Power", "a_vig"), ("a_Magic", "a_mag"), ("a_Attack", "a_atk"),
           ("a_Vitality", "a_sta"), ("a_Agility", "a_spd"), ("t_Defense", "t_def"), ("t_AbilityDefense", "t_mdef"),
           ("t_Vitality", "t_sta"), ("t_Power", "t_vig"), ("t_Level", "t_lv"), ("a_hp", "a_hp"), ("t_hp", "t_hp")]
LABEL = {"a_Level": "Lv", "a_Power": "Vigor", "a_Magic": "Magic", "a_Attack": "BP", "a_Vitality": "Stamina",
         "a_Agility": "Speed", "t_Defense": "tgt Def", "t_AbilityDefense": "tgt MDef", "t_Vitality": "tgt Stamina",
         "t_Power": "tgt Vigor", "t_Level": "tgt Lv", "a_hp": "HP", "t_hp": "tgt HP"}


def mean(r):
    for k in ("hit", "heal", "mp", "crit"):
        if r[f"{k}_n"]:
            return (r[f"{k}_min"] + r[f"{k}_max"]) / 2
    return None


def sensitivity(group):
    base = next((r for r in group if r["case"] == "base"), None)
    b = mean(base) if base else None
    if not b:
        return "no value in the base case"
    parts = []
    for prefix, col in FACTORS:
        pts = sorted((r[col], mean(r)) for r in group
                     if r["case"].startswith(prefix + "=") and ";" not in r["case"] and mean(r) is not None)
        if not pts:
            continue
        lo, hi = pts[0], pts[-1]
        if all(abs(m / b - 1) < 0.03 for _, m in pts):
            continue
        parts.append(f"{LABEL[prefix]} {lo[0]}->{lo[1] / b:.2f}x, {hi[0]}->{hi[1] / b:.2f}x")
    return f"base {b:.0f}; " + ("; ".join(parts) if parts else "no stat moves it")


# --- measured properties -------------------------------------------------------------------

def case_mean(group, case, raw=False):
    r = next((x for x in group if x["case"] == case), None)
    if not r:
        return None
    if raw and r.get("raw_min") not in (None, ""):
        return (r["raw_min"] + r["raw_max"]) / 2
    return mean(r)


def defense_mode(group, raw=False):
    """Which defense the game applied: "def", "mdef" or "none" (Def/MDef 0 vs 255 cases)."""
    d0, d255 = case_mean(group, "t_Defense=0", raw), case_mean(group, "t_Defense=255", raw)
    m0, m255 = case_mean(group, "t_AbilityDefense=0", raw), case_mean(group, "t_AbilityDefense=255", raw)
    if d0 and d255 is not None and d255 < 0.5 * d0:
        return "def"
    if m0 and m255 is not None and m255 < 0.5 * m0:
        return "mdef"
    if (d0 or 0) >= MAX_HIT or (m0 or 0) >= MAX_HIT:
        return "?"  # capped at 9999 with and without defense: can't tell
    return "none" if (d0 or m0) else "?"


def fixed_value(group):
    """True when neither Level nor Magic nor Vigor moves the value (item heals, fixed damage)."""
    vals = [case_mean(group, c) for c in ("base", "a_Level=1", "a_Level=99", "a_Magic=1", "a_Magic=200", "a_Power=1")]
    vals = [v for v in vals if v]
    return len(vals) >= 3 and max(vals) <= 1.05 * min(vals)


def random_value(group):
    """True when a case spreads far wider than any variance (Go Fish heals 0-303)."""
    for r in group:
        for k in ("hit", "heal"):
            if r[f"{k}_n"] and r[f"{k}_max"] > 3 * max(1, r[f"{k}_min"]) and r[f"{k}_max"] > 10:
                return True
    return False


def fit_bp(group):
    """Battle Power that makes vanilla's Fight formula fit the game's own values best."""
    pts = [(r, observed(r, False, True)) for r in group if r["a_atk"] == BASE_BP and ";" not in r["case"]]
    pts = [(r, o) for r, o in pts if o]
    best, err = BASE_BP, None
    for bp in range(1, 256):
        e = 0.0
        for r, o in pts:
            lo, hi = capped(apply(vanilla_variance(vanilla_fight(r["a_lv"], bp, r["a_vig"])), lambda d: defense(d, r["t_def"])))
            mid = (lo + hi) / 2
            e += ((o[0] + o[1]) / 2 / max(1, mid) - 1) ** 2
        if err is None or e < err:
            best, err = bp, e
    return best


# --- main -----------------------------------------------------------------------------------

def main(path):
    header, rows = read(path)
    data = json.load(open(os.path.join(MOD, "bnw_data.json"), encoding="utf-8"))
    spirit = set(data["spirit"]["magic_abilities"])
    WEAPON_FLAGS.update(data["spirit"].get("weapon_flag_abilities", []))
    specials = special_multipliers()
    rom_flags = {"vanilla": spell_flags(os.path.join(PR, "..", "rom", "vanilla", "Final Fantasy III (USA).sfc")),
                 "bnw": spell_flags(profiles.BraveNewWorld.rom)}
    ability_names = names()

    # did the stat overrides take? (case "a_Level=50" must show a_lv 50)
    col_of = dict(FACTORS)
    bad_override = defaultdict(int)
    for r in rows:
        for part in r["case"].split(";"):
            if "=" in part:
                k, v = part.split("=")
                col = col_of.get(k)
                if col and str(r[col]) != v:
                    bad_override[k] += 1

    groups = defaultdict(list)
    for r in rows:
        groups[(r["ability"], r["fn"], r["dir"])].append(r)

    # measured per ability function: defense met (and the cached BP for character Fight)
    flags = {("rom", rules, aid): f for rules in RULES for aid, f in rom_flags[rules].items()}
    flag_diffs = {rules: [] for rules in RULES}
    for key, group in groups.items():
        aid, fn_index, d = key
        mode = defense_mode(group)
        flags[key + ("bnw",)] = mode
        flags[key + ("vanilla",)] = defense_mode(group, raw=True)
        if group[0]["fn_type"] == 2 and group[0]["ability_type"] == 4 and d == "PE":
            flags[("bp", aid, fn_index)] = fit_bp(group)
        if group[0]["fn_type"] in (2, 3) and group[0]["ability_type"] not in (4, 15, 16) and mode in ("def", "mdef", "none"):
            for rules in RULES:
                if aid not in rom_flags[rules]:
                    continue
                ign, phys = rom_flags[rules][aid]
                want = "none" if ign else "def" if phys else "mdef"
                mode = flags[key + (rules,)]
                if mode in ("def", "mdef", "none") and want != mode:
                    nm = ability_names.get(aid, "") or "?"
                    flag_diffs[rules].append(f"{aid} {nm} ({'char->monster' if d == 'PE' else 'monster->char'}): "
                                             f"game meets {mode}, ROM says {want}")

    results = {rules: defaultdict(list) for rules in RULES}
    unchecked = []
    for key, group in sorted(groups.items()):
        aid, fn_index, d = key
        r0 = group[0]
        name = ability_names.get(aid, "") or "?"
        label = f"{aid} {name} [{FN_NAMES.get(r0['fn_type'], r0['fn_type'])}, {'char->monster' if d == 'PE' else 'monster->char'}]"
        if fixed_value(group) or random_value(group) or (expected(r0, "bnw", spirit, specials, flags)[0] is None
                                  and expected(r0, "vanilla", spirit, specials, flags)[0] is None):
            kind = expected(r0, "bnw", spirit, specials, flags)[1]
            if not kind.startswith("special without"):
                any_value = any(mean(r) is not None for r in group)
                unchecked.append(f"{label}: {sensitivity(group) if any_value else 'no value: ' + why_none(group)}")
            continue
        for rules in RULES:
            exp_kind = expected(r0, rules, spirit, specials, flags)[1]
            checked, unknown, fails = 0, 0, []
            for r in group:
                if "status=" in r["case"] or r["dir"] == "PP":
                    continue  # measured, not checked (see "Measured modifiers")
                exp, kind = expected(r, rules, spirit, specials, flags)
                if exp is None:
                    continue
                obs = observed(r, r["fn_type"] == 1, rules == "vanilla")
                if obs is None:
                    unknown += (r["hit_n"] or r["heal_n"]) > 0
                    continue
                checked += 1
                lo, hi = capped(exp)
                tol = max(2, 0.02 * hi)
                if obs[0] < lo - tol or obs[1] > hi + tol:
                    ratio = ((obs[0] + obs[1]) / 2) / max(1, (lo + hi) / 2)
                    fails.append((abs(ratio - 1), r["case"], obs, (lo, hi), ratio))
            res = results[rules]
            if checked == 0:
                res["no data" if unknown else "no hits"].append(
                    (key, f"{label} ({exp_kind}): " + ("raw value not recorded" if unknown else why_none(group))))
            elif fails:
                fails.sort(reverse=True)
                _, case, obs, exp, ratio = fails[0]
                res["fail"].append((key, f"{label} ({exp_kind}): {len(fails)}/{checked} cases off; worst {case}: "
                                         f"game {obs[0]}-{obs[1]}, expected {exp[0]}-{exp[1]} (x{ratio:.2f})"))
            else:
                res["pass"].append((key, f"{label} ({exp_kind}): {checked} cases"))

    out = ["# Formula probe report", ""]
    out += [f"- {h}" for h in header]
    out.append(f"- {len(rows)} rows, {len(groups)} ability functions x direction")
    bps = sorted({v for k, v in flags.items() if isinstance(k, tuple) and k and k[0] == "bp"})
    if bps:
        out.append(f"- character Fight: Battle Power the game's formula used (fitted; the probe set {BASE_BP}): {bps}")
    if bad_override:
        out.append(f"- **stat overrides that did not take** (cases): {dict(bad_override)}")
    out.append("")
    summary = []
    for rules, title in RULES.items():
        res = results[rules]
        counts = ", ".join(f"{st} {len(res.get(st, []))}" for st in STATUSES)
        summary.append(f"{rules}: {counts}")
        out += [f"## {title}", "", counts, ""]
        if flag_diffs[rules]:
            out += [f"### defense met differs from the ROM's spell flags: {len(flag_diffs[rules])}", ""]
            out += [f"- {t}" for t in sorted(set(flag_diffs[rules]))] + [""]
        for st in STATUSES:
            if res.get(st):
                out += [f"### {st}: {len(res[st])}", ""] + [f"- {t}" for _, t in res[st]] + [""]
    out += modifier_section(groups, ability_names)
    out += [f"## Unchecked function types: {len(unchecked)}", "",
            "No formula in the checker yet; which stats move the game's value (one stat at a time "
            "against the base case):", ""] + [f"- {t}" for t in unchecked] + [""]
    with open(REPORT, "w", encoding="utf-8") as f:
        f.write("\n".join(out))
    print("probe: " + " | ".join(summary) + f" | unchecked {len(unchecked)} -> {REPORT}")
    return 1 if any(results[r].get("fail") for r in RULES) else 0


RULES = {"vanilla": "Vanilla FF6 (SNES) vs the game's own values: the baseline for every port",
         "bnw": "Brave New World vs the final values (after the plugin)"}
STATUSES = ("fail", "pass", "no hits", "no data")


def modifier_section(groups, ability_names):
    """Status cases and character-on-character hits, as multipliers on the base case: the
    game's own (raw, BNW rules off) and the final one."""
    out = ["## Measured modifiers (status cases, character -> character)", "",
           "Each line: case mean / base case mean, raw (BNW rules off) and final. PP = a character "
           "hitting another character; PP base is compared with EP base (same target stats).", ""]
    def m(r, raw):
        v = case_mean([r], r["case"], raw)
        return v
    for key, group in sorted(groups.items()):
        aid, fn_index, d = key
        status = [r for r in group if r["case"].startswith(("a_status=", "t_status="))]
        if not status and d != "PP":
            continue
        base = next((r for r in group if r["case"] == "base"), None)
        if not base or mean(base) is None:
            continue
        name = ability_names.get(aid, "") or "?"
        parts = []
        if d == "PP":
            ep = groups.get((aid, fn_index, "EP"))
            epb = next((r for r in ep or [] if r["case"] == "base"), None)
            if epb and mean(epb):
                parts.append(f"PP/EP raw x{m(base, True) / max(1, m(epb, True)):.2f} final x{mean(base) / max(1, mean(epb)):.2f}")
        for r in status:
            if mean(r) is None:
                parts.append(f"{r['case']}: no value")
                continue
            parts.append(f"{r['case'][2:].replace('status=', '')}{'@a' if r['case'].startswith('a_') else '@t'} "
                         f"raw x{m(r, True) / max(1, m(base, True)):.2f} final x{mean(r) / max(1, mean(base)):.2f}")
        out.append(f"- {aid} {name} [{d}]: " + "; ".join(parts))
    return out + [""]


def why_none(group):
    t = defaultdict(int)
    for r in group:
        for k in ("miss_n", "zero_n", "other_n", "err_n", "crit_n", "mp_n"):
            t[k[:-2]] += r[k] or 0
    return ", ".join(f"{k} {v}" for k, v in t.items() if v) or "nothing"


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else DEFAULT))
