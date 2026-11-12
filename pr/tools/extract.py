"""Extract every text asset (CSV, TXT, JSON, BYTES) from FF6 Pixel Remaster's
asset bundles into Magicite's folder layout:

    <out>/<group>/<container path>          e.g. master/Assets/GameAssets/Serial/Data/Master/item.csv
    <out>/<group>/keys/Export.json          {"keys": [...], "values": [...]} like Magicite's export

<group> is the bundle name before "_assets_all_" (Magicite's group name).

  .venv/Scripts/python tools/extract.py [--game <FF6 PR folder>] [--out vanilla]
  .venv/Scripts/python tools/extract.py --game-id ff5      any Pixel Remaster (tools/prkit/games.py
                                                           finds it; out: pr/games/ff5/vanilla)
"""
import argparse
import glob
import json
import os
import sys
from collections import defaultdict

import UnityPy

DEFAULT_GAME = r"D:/SteamLibrary/steamapps/common/FINAL FANTASY VI PR"
BUNDLES = "FINAL FANTASY VI_Data/StreamingAssets/aa/StandaloneWindows64"


def group_name(bundle_path):
    name = os.path.basename(bundle_path)
    return name.split("_assets_all_")[0] if "_assets_all_" in name else os.path.splitext(name)[0]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=None, help="the game's folder (default: FF6 PR's on this PC)")
    ap.add_argument("--game-id", default=None, help="ff1..ff6: the game by id (tools/prkit/games.py)")
    ap.add_argument("--out", default=None)
    ap.add_argument("--audio", action="store_true", help="also extract music/sound (.bytes, ~800 MB)")
    args = ap.parse_args()
    if args.game_id:
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        from prkit import games
        g = games.get(args.game_id)
        if not g.installed:
            sys.exit(f"{g.title} PR isn't installed (tools/kit.py games)")
        bundle_dir = g.bundles
        args.out = args.out or g.vanilla
    else:
        bundle_dir = os.path.join(args.game or DEFAULT_GAME, BUNDLES)
        args.out = args.out or os.path.join(os.path.dirname(__file__), "..", "vanilla")

    bundles = sorted(glob.glob(os.path.join(bundle_dir, "*.bundle")))
    if not bundles:
        sys.exit(f"no bundles in {bundle_dir}")
    keys = defaultdict(dict)
    count = 0
    for n, path in enumerate(bundles, 1):
        group = group_name(path)
        if not args.audio and (group.endswith("_bgm") or group.endswith("_se") or group == "soundplayer"):
            continue
        env = UnityPy.load(path)
        for container, obj in env.container.items():
            try:
                if obj.type.name != "TextAsset":
                    continue
            except ValueError:  # null pointer entries in a few bundles
                continue
            data = obj.read()
            raw = data.m_Script
            raw = raw.encode("utf-8", "surrogateescape") if isinstance(raw, str) else bytes(raw)
            dest = os.path.join(args.out, group, *container.split("/"))
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with open(dest, "wb") as f:
                f.write(raw)
            stem = os.path.splitext(container)[0]
            keys[group][os.path.basename(stem)] = stem
            count += 1
        if n % 200 == 0:
            print(f"{n}/{len(bundles)} bundles, {count} text assets")
    for group, mapping in keys.items():
        os.makedirs(os.path.join(args.out, group, "keys"), exist_ok=True)
        with open(os.path.join(args.out, group, "keys", "Export.json"), "w", encoding="utf-8") as f:
            json.dump({"keys": list(mapping), "values": list(mapping.values())}, f, indent=2)
    print(f"done: {count} text assets from {len(keys)} groups ({len(bundles)} bundles scanned)")


if __name__ == "__main__":
    main()
