"""Audit: the check NPCs WC re-skins (tools/wc_npcs.py PAIRS) against the animations their map's
scripts play on them.

A re-skinned entity shows another sprite sheet (a character's, or a soldier/imp/merchant/ghost
/magicite for an esper or item reward). A script that cues an animation on it by name needs that
animation on the new sheet: a script's own Animations (custom ones, whose frames name sprites of
the original sheet) or the sheet's anim_info. One the new sheet lacks stalls the field: the
Ancient Castle's queen room (sc_map_30811_7 cues its own 'stone', frame Stone_00 of the queen's
sheet) stayed black with the statue showing Gogo (the user's Deck, 2026-11-12).

Lists each animation a script plays on a re-skinned entity and the replacement sheets that lack
it (frames judged by each sheet's anim_info, the only frame list extracted).

    .venv/Scripts/python tools/wc_npc_anim_audit.py
"""
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import prdata as P  # noqa: E402
import wc_npcs  # noqa: E402

PLAYS = ("CueAnim", "PlayAnim")


def mapobjects():
    out = {}
    with open(f"{P.VANILLA}/master/Assets/GameAssets/Serial/Data/Master/mapobject.csv", encoding="utf-8") as f:
        next(f)
        for line in f:
            c = line.strip().split(",")
            out[int(c[0])] = c[1]
    return out


def sheet(asset_name):
    """{anim name: [frame sprite names]} of a map sprite sheet (anim_info.json)."""
    hits = glob.glob(f"{P.VANILLA}/{asset_name}/Assets/GameAssets/Serial/Res/Chara/Map/*/anim_info.json")
    if not hits:
        return None
    d = json.load(open(hits[0], encoding="utf-8"))
    return {a["name"]: [fr["sprite_name"] for fr in a["frames"]] for a in d["animation"]}


def entity_of(rel, object_id):
    d = json.load(open(f"{P.VANILLA}/{rel}", encoding="utf-8"))
    for layer in d["layers"]:
        for o in layer.get("objects", []):
            if o["id"] == object_id:
                pr = {p["name"]: p["value"] for p in o.get("properties", [])}
                return pr.get("entity_id"), pr.get("asset_id")
    return None, None


def played_on(script, entity):
    """(index, mnemonic, name) of each animation a puppet routine of `entity` plays."""
    ms = script["Mnemonics"]
    out = []
    for m in ms:
        if m["mnemonic"] != "SetPuppet" or m["operands"]["iValues"][1] != entity:
            continue
        at = m["operands"]["iValues"][2] + 1      # the VM goes on after the labelled Nop
        while at < len(ms) and ms[at]["mnemonic"] not in ("Exit", "Return"):
            if ms[at]["mnemonic"] in PLAYS:
                out.append((at, ms[at]["mnemonic"], (ms[at]["operands"]["sValues"] or [""])[0]))
            at += 1
    return out


def main():
    mo = mapobjects()
    replacements = sorted(set(wc_npcs.CHARACTER_NPC.values()) | set(wc_npcs.WC_SPRITE_NPC.values()))
    sheets = {a: sheet(mo[a]) for a in replacements if a in mo}
    problems = 0
    for rel in sorted({f for pairs in wc_npcs.PAIRS.values() for f, _ in pairs}):
        objs = [o for pairs in wc_npcs.PAIRS.values() for f, o in pairs if f == rel]
        folder = os.path.dirname(f"{P.VANILLA}/{rel}")
        for obj in sorted(set(objs)):
            entity, asset = entity_of(rel, obj)
            if entity is None:
                print(f"{rel} object {obj}: not found")
                continue
            for sp in sorted(glob.glob(f"{folder}/sc_*.json")):
                sc = json.load(open(sp, encoding="utf-8"))
                custom = {a["name"]: [fr["sprite_name"] for fr in a["frames"]] for a in sc.get("Animations") or []}
                for at, mn, name in played_on(sc, entity):
                    if name in custom:
                        need = set(custom[name])
                        lack = [a for a, s in sheets.items() if s is not None and not need <= {x for fr in s.values() for x in fr}]
                        kind = f"custom, frames {sorted(need)}"
                    else:
                        lack = [a for a, s in sheets.items() if s is not None and name not in s]
                        kind = "sheet animation"
                    flag = "LACKING" if lack else "ok"
                    problems += bool(lack)
                    print(f"{flag:8} {os.path.basename(sp)}[{at}] {mn} '{name}' ({kind}) on entity {entity} "
                          f"(asset {asset} {mo.get(asset, '?')}; {rel.split('/')[0]})"
                          + (f": missing on {len(lack)} of {len(sheets)} sheets {lack}" if lack else ""))
    print(f"{problems} animation(s) a re-skin can't play")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
