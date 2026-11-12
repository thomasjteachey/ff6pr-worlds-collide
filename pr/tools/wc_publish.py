"""The public Worlds Collide repository: this project's WC parts, copied into their own git tree.

    .venv/Scripts/python tools/wc_publish.py [OUT]     (default: ../../ff6pr-worlds-collide)

Copies the WC plugin, the tools that build it and its generator (all of tools/*.py: the bundle
ships them), prkit, the WC tests and the docs into OUT with the project's layout (pr/..., mods/...),
writes its README, .gitignore and requirements, and records WC itself (github.com/ff6wc/WorldsCollide)
as a submodule at the commit this port uses. OUT's .git is left alone: commit and push from there.

Never the game's files (the user, 2026-11-12: the mods are distributed): no ROM, no pr/vanilla, no
decompiled code, no build output, and every copied file is checked against the game's extract
(prkit/package.py game_files_in), as the player zip is.
"""
import fnmatch
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PR = os.path.dirname(HERE)
PROJECT = os.path.dirname(PR)
sys.path.insert(0, HERE)
from prkit import package  # noqa: E402
import bnw_text  # noqa: E402

DEFAULT_OUT = os.path.join(os.path.dirname(PROJECT), "ff6pr-worlds-collide")
WC = os.path.join(PROJECT, "mods", "worlds-collide")
WC_URL = "https://github.com/ff6wc/WorldsCollide.git"

# (source under the project, glob patterns inside it; "**" = the whole folder)
PARTS = [
    ("pr/plugin/WcPlugin", ["**"]),
    ("pr/plugin/Shared", ["**"]),
    ("pr/plugin/Wc.Tests", ["**"]),
    ("pr/plugin/WcAi.Tests", ["**"]),
    ("pr/tools", ["*.py", "*.sh", "*.ps1", "wc_basedata_regions.json"]),
    ("pr/tools/prkit", ["**"]),
    ("pr/tools/ghidra", ["**"]),
    ("pr/tools/TypeDump", ["**"]),
    ("pr/tests", ["test_wc.py", "test_wc_romfree.py", "test_wc_xref.py", "test_prkit.py"]),
    ("pr", ["modkit.json", "MODDING.md", "PORTING.md"]),
    ("mods", ["worlds-collide-presets.json"]),
]
SKIP_DIRS = {"bin", "obj", "__pycache__", ".vs", ".git"}
SKIP_EXT = (".sfc", ".smc", ".pyc", ".bin", ".dll", ".exe", ".pdb", ".zip", ".log")

README = """# Worlds Collide for FINAL FANTASY VI Pixel Remaster

[Worlds Collide](https://github.com/ff6wc/WorldsCollide) {wc_version}, the FF6 open-world randomizer,
played in the Steam Pixel Remaster: make seeds in the game with every option of the website
(presets, pasted flag strings, a seed field), play them, switch between them; saves remember
their seed. Seeds match the website's for the same seed and flags.

## Play

Download `WorldsCollide-PR.zip` from the [latest release](../../releases/latest) and follow its
README: BepInEx 6 (IL2CPP, bleeding edge) and Magicite first, then extract the zip into the
game folder. Steam Deck / Linux: add `WINEDLLOVERRIDES="winhttp=n,b" %command%` to the game's
launch options. The first start takes about ten seconds longer: the mod makes the game data it
works from out of your own copy of the game (no game files are distributed).

On the title screen choose **Worlds Collide**, pick a preset or paste flags, then
**Generate and play this seed** and start a New Game.

## How it works

- `pr/plugin/WcPlugin`: the BepInEx plugin (the in-game seed screen, live data apply, WC's
  events, objectives, the track menu, WC's battle rules and the monster AI WC writes).
- `pr/tools`: the generator, run in the game with a bundled Python: WC's own code (the
  `mods/worlds-collide` submodule, untouched) on the ROM's data tables, then the porter that
  turns its output into Pixel Remaster data. `pr/PORTING.md` is the full development log,
  `pr/MODDING.md` the tooling guide.
- No ROM and no game files: `pr/tools/wc_gamedata.py` + `pr/tools/pr_bundle.py` make the copies
  the mod works from on the player's machine at first start.

## Build

Needs Windows, FF6 PR with BepInEx 6 and Magicite installed, .NET 6 SDK, Python 3.12, and the US
v1.0 SNES ROM (`rom/vanilla/Final Fantasy III (USA).sfc`, for WC's data tables; never shipped).

    git clone --recurse-submodules <this repo>
    cd pr && python -m venv .venv && .venv/Scripts/pip install -r requirements.txt
    .venv/Scripts/python tools/extract.py --game "<your FF6 PR folder>"     # pr/vanilla
    bash tools/deploy.sh --wc --pc                                         # build + install
    .venv/Scripts/python tools/package_mod.py wc                           # build/dist zip

The plugin builds against your game's BepInEx (`-p:GameDir=<folder>` if it isn't
`D:\\SteamLibrary\\steamapps\\common\\FINAL FANTASY VI PR`).

## Credits

Worlds Collide by AtmaTek and its contributors (MIT). BepInEx, Il2CppInterop, HarmonyX,
Magicite by Silvris. FINAL FANTASY VI is Square Enix's; this is an unofficial fan mod.
"""

GITIGNORE = """bin/
obj/
__pycache__/
*.pyc
.venv/
.vs/
pr/vanilla/
pr/build/
pr/cache/
pr/data/wc_base.bin
rom/
build/
*.sfc
*.smc
"""

REQUIREMENTS = "UnityPy>=1.20  # tools/extract.py (pr/vanilla from the game's bundles)\nPillow  # tools/snes_harness.py\n"


def files():
    for src, patterns in PARTS:
        root = os.path.join(PROJECT, *src.split("/"))
        if patterns != ["**"]:      # files of that folder itself
            for f in sorted(os.listdir(root)):
                p = os.path.join(root, f)
                if os.path.isfile(p) and not f.lower().endswith(SKIP_EXT) and any(fnmatch.fnmatch(f, x) for x in patterns):
                    yield p, f"{src}/{f}"
            continue
        for r, dirs, fs in os.walk(root):
            dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS)
            rel_dir = os.path.relpath(r, root).replace("\\", "/")
            for f in sorted(fs):
                if f.lower().endswith(SKIP_EXT):
                    continue
                rel = f if rel_dir == "." else f"{rel_dir}/{f}"
                yield os.path.join(r, f), f"{src}/{rel}"


def wc_commit():
    return subprocess.run(["git", "-C", WC, "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()


def main():
    out = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else DEFAULT_OUT)
    entries = list(files())
    entries.append((bnw_text.VWF_TBL, "pr/data/vwf.tbl"))      # the menu font's text table
    hits = package.game_files_in(entries, os.path.join(PR, "vanilla"))
    if hits:
        raise SystemExit(f"publish: {len(hits)} of the game's own files, not publishing: {hits[:5]}")
    big = [(n, os.path.getsize(s)) for s, n in entries if os.path.getsize(s) > 2 << 20]
    if big:
        raise SystemExit(f"publish: unexpectedly large files: {big}")
    os.makedirs(out, exist_ok=True)
    for name in os.listdir(out):      # everything but .git: the tree is made afresh
        if name in (".git", ".gitmodules"):
            continue
        p = os.path.join(out, name)
        shutil.rmtree(p) if os.path.isdir(p) else os.remove(p)
    for src, name in entries:
        dst = os.path.join(out, *name.split("/"))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)
    version = "1.4.3"
    try:
        sys.path.insert(0, WC)
        import version as wcv  # noqa: E402  (WC's own version.py)
        version = wcv.__version__
    except Exception:
        pass
    with open(os.path.join(out, "README.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(README.format(wc_version=version))
    with open(os.path.join(out, ".gitignore"), "w", encoding="utf-8", newline="\n") as f:
        f.write(GITIGNORE)
    with open(os.path.join(out, "pr", "requirements.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(REQUIREMENTS)
    with open(os.path.join(out, ".gitmodules"), "w", encoding="utf-8", newline="\n") as f:
        f.write(f'[submodule "mods/worlds-collide"]\n\tpath = mods/worlds-collide\n\turl = {WC_URL}\n')
    commit = wc_commit()
    # the submodule's folder, empty until `git submodule update --init` (missing, `git add -A` drops it)
    os.makedirs(os.path.join(out, "mods", "worlds-collide"), exist_ok=True)
    if os.path.isdir(os.path.join(out, ".git")):
        subprocess.run(["git", "-C", out, "update-index", "--add", "--cacheinfo", f"160000,{commit},mods/worlds-collide"],
                       check=True)
    size = sum(os.path.getsize(s) for s, _ in entries)
    print(f"publish: {len(entries)} files ({size // 1024} KB) + WC {version} at {commit[:10]} as a submodule -> {out}")


if __name__ == "__main__":
    main()
