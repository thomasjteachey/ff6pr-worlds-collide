"""BNW's own event fights (the story's scripted battles).

The PR's event scripts start their battles by party (Encount / EncountBoss [party]), parties the
PR made for vanilla's formations (idmap.party_formation). The SNES events call two-formation
packs instead (event command $4D <pack> <background>, the packs at PACK2), and BNW changed which
formations some packs hold: its South Figaro Grunts are Soldiers and a Phalanx, its Balloons
Grenades, Owzer's Still Life three Nightshades. So for each PR event party: vanilla's formation ->
the pack entries holding it -> BNW's formation in the same entry. An entry counts when both ROMs'
event code still calls that pack at the same address ("4D <pack>" anchored), so a pack BNW gave to
something else (and whose event moved to another pack) doesn't drag the PR's event along.

A clean case gets a party of BNW's line-up on the event party's own look and settings
(background, music, battle script, escape: EncounterPorter.party_row; id 7000 + the event's
party), and the event scripts call it. Clean: one BNW formation, every monster in it present at
the start (hidden ones are brought in by the AI or a battle event: per-boss work), the line-up
differs from vanilla's, the fight runs one of the PR's generic battle scripts (GENERIC: no
monster-specific battle event), and it looks like the same fight (a monster in common, one of
vanilla's monsters gone from BNW, or renamed at its slot). BNW also moved monsters: its Doberman
left slot 26 for 113 and a Banshee took 26, so the PR's Doberman fight had faced Banshees. Monster-in-a-box scripts are
port_chests'. Everything else stays the PR's (vanilla line-up, BNW's stats where BNW kept the
monster) and is logged with its reason.
"""
import copy
import json
import os
from collections import defaultdict

import ff6snes as S
import idmap as M
import port_chests as C
import port_encounters as E
import prdata as P

EVENT_PARTY_BASE = 7000
EVENT_CODE = (0x0A0000, 0x0CE600)
# the PR's battle scripts that handle any monsters (party.script_name: script.csv ids)
GENERIC = {"0", "8010", "8021", "9006", "9026"}   # none, sc_b_0239, sc_b_timer, sc_b_2006, sc_b_2026
BATTLES = ("Encount", "EncountBoss")


def slots(rom, f):
    """Formation f's six slots: (enemy id, present at the start) for slots holding a monster."""
    r = rom.data[E.FORMATIONS + 15 * f:E.FORMATIONS + 15 * f + 15]
    out = []
    for s in range(6):
        e = r[2 + s] | ((r[14] >> s & 1) << 8)
        if e != 0x1FF:
            out.append((e, bool(r[1] >> s & 1)))
    return out


def pack2(rom, p):
    a = rom.data[C.PACK2 + 4 * p: C.PACK2 + 4 * p + 4]
    return (int.from_bytes(a[0:2], "little") & 0x7FFF, int.from_bytes(a[2:4], "little") & 0x7FFF)


def anchored_packs(van, bnw):
    """Packs both ROMs' event code calls at the same address ($4D <pack>)."""
    a, b = van.data, bnw.data
    out = set()
    for i in range(*EVENT_CODE):
        if a[i] == 0x4D and b[i] == 0x4D and a[i + 1] == b[i + 1]:
            out.add(a[i + 1])
    return out


def event_battles():
    """{party: [script name]} for the battles the PR's map scripts start."""
    import glob
    out = defaultdict(set)
    for f in glob.glob(os.path.join(P.VANILLA, "map_*", "**", "sc_*.json"), recursive=True):
        for m in json.load(open(f, encoding="utf-8")).get("Mnemonics", []):
            if m.get("mnemonic") in BATTLES:
                out[m["operands"]["iValues"][0]].add(os.path.basename(f)[:-5])
    return {k: sorted(v) for k, v in out.items()}


def port(van, bnw, tables, porter, out_dir, log):
    import pr_script as Sc
    mp = tables["monster_party"]
    pf = M.party_formation()
    names_v = [S.decode_name(x).strip() for x in van.table(*S.ENEMY_NAMES)]
    names_b = [S.decode_name(x).strip() for x in bnw.table(*S.ENEMY_NAMES)]
    anchors = anchored_packs(van, bnw)
    entries = defaultdict(list)                      # vanilla formation -> [(pack, position)]
    for p in range(256):
        for i, f in enumerate(pack2(van, p)):
            entries[f].append((p, i))
    boxes = set(C.miab_scripts())
    battles = event_battles()
    remap, skipped = {}, {}
    for party, scripts in sorted(battles.items()):
        scripts = [s for s in scripts if s not in boxes]
        fv, row = pf.get(party), mp.rows.get(party)
        if not scripts or fv is None or row is None:
            continue
        vs = slots(van, fv)
        cands = sorted({pack2(bnw, p)[i] for p, i in entries.get(fv, []) if p in anchors})
        cands = [f for f in cands if sorted(e for e, _ in slots(bnw, f)) != sorted(e for e, _ in vs)]
        if len(cands) > 1:
            cands = [f for f in cands if slots(bnw, f)] or cands   # an empty filler entry (formation 0)
        if not cands:
            continue                                  # BNW fights the same line-up here (or no pack)
        why = None
        fb = cands[0] if len(cands) == 1 else None
        bs = slots(bnw, fb) if fb is not None else []
        if fb is None:
            why = f"BNW formations {cands}: more than one"
        elif not bs:
            why = f"BNW formation {fb} is empty"
        elif not all(present for _, present in bs):
            why = f"BNW formation {fb} brings monsters in later"
        elif row.get("script_name", "0") not in GENERIC:
            why = f"its own battle script ({row.get('script_name')})"
        else:
            vnames = {names_v[e] for e, _ in vs}
            bnames = {names_b[e] for e, _ in bs}
            renamed = {names_b[e] for e, _ in vs}
            gone = any(S.blank_enemy(bnw, e) for e, _ in vs)   # BNW can't stage vanilla's fight
            if not (vnames & bnames or gone or renamed & bnames):
                why = f"BNW formation {fb} ({', '.join(sorted(bnames))}) doesn't look like the same fight"
        label = f"party {party} ({', '.join(names_v[e] for e, _ in vs)}; {', '.join(scripts)})"
        if why:
            skipped[label] = why
            continue
        pid = EVENT_PARTY_BASE + party
        new = porter.party_row(fb, row, pid)
        # escaping as port.port_escape decides it: a monster that can't be run from (SNES byte 19
        # bit 3) makes the party not_escape 2; the PR's special 3 stays
        if new["not_escape"] != "3":
            new["not_escape"] = "2" if any(bnw.data[0x0F0000 + 32 * e + 19] & 0x08 for e, _ in bs) else "0"
        mp.rows[pid] = new
        remap[party] = (pid, fb, scripts)
        log(f"  event fight {label} -> BNW formation {fb}: {', '.join(names_b[e] for e, _ in bs)} (party {pid})")
    # the event scripts call the new parties
    keys = C.asset_keys()
    written = defaultdict(dict)
    by_script = defaultdict(list)
    for party, (pid, fb, scripts) in remap.items():
        for s in scripts:
            by_script[s].append((party, pid))
    for name, pairs in sorted(by_script.items()):
        rel = Sc.rel(name)
        dest = os.path.join(out_dir, *rel.split("/"))
        sc = json.load(open(dest, encoding="utf-8")) if os.path.exists(dest) else copy.deepcopy(Sc.load(name))
        n = 0
        for m in sc.get("Mnemonics", []):
            if m.get("mnemonic") in BATTLES:
                for party, pid in pairs:
                    if m["operands"]["iValues"][0] == party:
                        m["operands"]["iValues"][0] = pid
                        n += 1
        if n:
            Sc.save(sc, out_dir, name)
            group, path = rel.split("/", 1)
            stem = os.path.splitext(path)[0]
            written[group][keys.get((group, stem), stem.split("/")[-2] + "/" + stem.split("/")[-1])] = stem
    for group, kv in written.items():
        kdir = os.path.join(out_dir, group, "keys")
        os.makedirs(kdir, exist_ok=True)
        with open(os.path.join(kdir, "bnw_events.json"), "w", encoding="utf-8") as fh:
            json.dump({"keys": list(kv), "values": list(kv.values())}, fh, indent=1)
    log(f"event fights: {len(remap)} fight BNW's formations ({sum(len(v) for v in written.values())} scripts); "
        f"{len(skipped)} keep the PR's line-up:")
    for label, why in sorted(skipped.items()):
        log(f"  kept {label}: {why}")
    return {"monster_party": {pid for pid, _, _ in remap.values()}}
