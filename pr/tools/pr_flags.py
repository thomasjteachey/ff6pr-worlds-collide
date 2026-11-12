"""Catalog of the PR's story flags (ScenarioFlag1-4, TreasureFlag1-4): for each flag, the event
scripts that set/reset it and the ones that test it, with the nearest code label and the
script's title (both Japanese, usually descriptive) and the first message key nearby. Used to
map Worlds Collide's SNES event bits to PR flags by meaning: the PR renumbered them
(ScenarioFlag1 5 is set in the Narshe raid, WC's bit 0x005 is NAMED_SABIN).

  .venv/Scripts/python tools/pr_flags.py [--out FILE] [Category:index ...]
"""
import argparse
import collections
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import prdata as P  # noqa: E402

FLAG_KINDS = ("ScenarioFlag", "TreasureFlag")


def scripts():
    for f in sorted(glob.glob(os.path.join(P.VANILLA, "map_*", "**", "s*.json"), recursive=True)):
        try:
            yield f, json.load(open(f, encoding="utf-8"))
        except (ValueError, UnicodeDecodeError):
            continue


def catalog():
    """{(category, index): {"set": [...], "reset": [...], "test": [...]}} with entries
    (script, map folder, title, label, first nearby message key)."""
    out = collections.defaultdict(lambda: {"set": [], "reset": [], "test": []})
    for f, d in scripts():
        ms = d.get("Mnemonics") or []
        name = d.get("Name") or os.path.basename(f)[:-5]
        folder = os.path.basename(os.path.dirname(f))
        title = (d.get("Title") or {}).get("main", "")
        label = ""
        for n, m in enumerate(ms):
            if m.get("label"):
                label = m["label"]
            o = m.get("operands") or {}
            sv, iv = o.get("sValues") or [], o.get("iValues") or []
            if not sv or not iv or not str(sv[0]).startswith(FLAG_KINDS):
                continue
            kind = {"SetFlag": "set", "ResetFlag": "reset", "Branch": "test"}.get(m["mnemonic"])
            if kind is None:
                continue
            msg = next((x["operands"]["sValues"][0] for x in ms[n:n + 40]
                        if x["mnemonic"] in ("Msg", "Select") and (x.get("operands") or {}).get("sValues")), "")
            entry = {"script": name, "map": folder, "title": title, "label": label, "at": n, "msg": msg}
            if kind == "test":
                entry["value"] = iv[1]
            out[(sv[0], iv[0])][kind].append(entry)
    return out


def describe(key, rec, limit=6):
    lines = [f"{key[0]}:{key[1]}  set {len(rec['set'])}, reset {len(rec['reset'])}, tested {len(rec['test'])}"]
    for kind in ("set", "reset", "test"):
        for e in rec[kind][:limit]:
            v = f" =={e['value']}" if kind == "test" else ""
            lines.append(f"   {kind:5s}{v} {e['script']} [{e['map']}] {e['title']} / {e['label']} {e['msg']}")
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out")
    ap.add_argument("flags", nargs="*")
    a = ap.parse_args()
    cat = catalog()
    if a.out:
        keys = sorted(cat, key=lambda k: (k[0], k[1]))
        with open(a.out, "w", encoding="utf-8") as f:
            for k in keys:
                f.write(describe(k, cat[k], limit=1000) + "\n")
        print(f"{len(keys)} flags -> {a.out}")
    for s in a.flags:
        c, i = s.split(":")
        print(describe((c, int(i)), cat.get((c, int(i)), {"set": [], "reset": [], "test": []})))


if __name__ == "__main__":
    main()
