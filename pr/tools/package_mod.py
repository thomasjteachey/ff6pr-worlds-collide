"""Package one mod for players: a zip to extract into the game folder (next to its .exe).

    .venv/Scripts/python tools/package_mod.py bnw|wc|<any mod in pr/modkit.json> [--out build/dist]

Same as `tools/kit.py package <mod>`: the mod's parts come from pr/modkit.json (tools/prkit/package.py
writes the zip). Brave New World and Worlds Collide are separate mods (the user, 2026-11-07: "WC/BNW
need to be separate mods entirely"): each package holds that mod only, its BepInEx plugin and its
Magicite folder, laid out as the game folder has them:

    BepInEx/plugins/<Mod>/...                                   the plugin (DLL + its data)
    FINAL FANTASY VI_Data/StreamingAssets/Magicite/<Mod>/...    the mod's game data (Magicite)
    <Mod>-README.txt

Built from what tools/deploy.sh installs. Never a ROM. Needs BepInEx 6 (IL2CPP) and Magicite
installed in the game first.
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from prkit import mods, package  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("mod", choices=sorted(mods.load()))
    ap.add_argument("--out", default=None)
    a = ap.parse_args()
    try:
        out, n = package.package(a.mod, a.out)
    except (FileNotFoundError, ValueError) as e:
        print(e)
        return 1
    print(f"{out}: {n} files ({os.path.getsize(out) // 1024} KB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
