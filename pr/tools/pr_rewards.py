"""Where the PR's event scripts hand out rewards: character joins (SysCall メンバー加入：X),
GetItem (with the item's name), and the "Obtained ..." lines, per script, with the map and the
nearby flags. For finding a WC check's reward point quickly.

  .venv/Scripts/python tools/pr_rewards.py [--map Map_20040] [--text REGEX] [--joins]
"""
import argparse
import glob
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import prdata as P  # noqa: E402


def texts():
    out = {}
    for f in ("story_mes_en", "system_en"):
        path = f"{P.VANILLA}/message/Assets/GameAssets/Serial/Data/Message/{f}.txt"
        for line in open(path, encoding="utf-8"):
            if "\t" in line:
                k, v = line.rstrip("\n").split("\t", 1)
                out[k] = v
    return out


def content_names(text):
    names = {}
    for r in P.Table("content"):
        names[r["id"]] = text.get(r["mes_id_name"], r["mes_id_name"])
    return names


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--map")
    ap.add_argument("--text")
    ap.add_argument("--joins", action="store_true")
    a = ap.parse_args()
    text = texts()
    names = content_names(text)
    maps = {r["map_name"]: r["id"] for r in P.Table("map")}
    areas = {r["id"]: text.get(r["area_name"], r["area_name"]) for r in P.Table("area")}
    area_of = {r["map_name"]: areas.get(r["area_id"], "?") for r in P.Table("map")}
    for f in sorted(glob.glob(os.path.join(P.VANILLA, "map_*", "**", "sc_e_*.json"), recursive=True)):
        folder = os.path.basename(os.path.dirname(f))
        if a.map and folder != a.map:
            continue
        d = json.load(open(f, encoding="utf-8"))
        ms = d["Mnemonics"]
        hits = []
        for n, m in enumerate(ms):
            o = m.get("operands") or {}
            iv, sv = o.get("iValues") or [], [x for x in (o.get("sValues") or []) if x]
            if m["mnemonic"] == "SysCall" and sv and sv[0].startswith("メンバー加入"):
                hits.append(f"{n} JOIN {sv[0].split('：')[1]}")
            elif m["mnemonic"] == "GetItem" and not a.joins:
                hits.append(f"{n} GetItem {iv[0]} {names.get(iv[0], '?')} x{iv[1]}")
            elif m["mnemonic"] == "Msg" and sv and re.search(r"Obtained|joined", text.get(sv[0], "")) and not a.joins:
                hits.append(f"{n} Msg {text[sv[0]][:50]}")
            elif m["mnemonic"] in ("SetFlag",) and hits:
                hits.append(f"{n} SetFlag {sv[0] if sv else ''}:{iv[0]}")
        if not hits:
            continue
        if a.text:
            body = " ".join(text.get(x["operands"]["sValues"][0], "") for x in ms
                            if x["mnemonic"] == "Msg" and (x.get("operands") or {}).get("sValues"))
            if not re.search(a.text, body, re.I):
                continue
        print(f"{os.path.basename(f)[:-5]} [{folder} = map {maps.get(folder, '?')}, {area_of.get(folder, '?')}]: " + "; ".join(hits))


if __name__ == "__main__":
    main()
