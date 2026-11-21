"""The public Worlds Collide repository (ff6pr-worlds-collide): this project's WC parts, copied
into their own git tree, with the modding kit as its kit/ submodule.

    .venv/Scripts/python tools/wc_publish.py [OUT]     (default: ../../ff6pr-worlds-collide)

Copies the WC plugin, WC's tools and tests and its presets into OUT with the project's layout
(pr/..., mods/...), writes its README, .gitignore, requirements and manifest, and records two
submodules: WC itself (github.com/ff6wc/WorldsCollide, mods/worlds-collide) at the commit this port
uses, and the kit (ff6pr-kit, kit/) at the kit repository's current commit, so publish and commit
the kit first (tools/kit_publish.py). Nothing of Brave New World's (the user, 2026-11-19: "make sure
WC and BNW are different repositories"). OUT's .git is left alone: commit and push from there.
The checks every public copy gets are tools/repo_publish.py's.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import kitpath  # noqa: E402,F401  (the modding kit's tools, when it's this repository's kit/ submodule)
import repo_publish as R  # noqa: E402

DEFAULT_OUT = os.path.join(os.path.dirname(R.PROJECT), "ff6pr-worlds-collide")
WC = os.path.join(R.PROJECT, "mods", "worlds-collide")
WC_URL = "https://github.com/ff6wc/WorldsCollide.git"

PARTS = [
    ("pr/plugin/WcPlugin", ["**"]),
    ("pr/plugin/Wc.Tests", ["**"]),
    ("pr/plugin/WcAi.Tests", ["**"]),
    ("pr/tools", R.WC_TOOLS + ["wc_publish.py"]),
    ("pr/tests", ["test_wc.py", "test_wc_romfree.py", "test_wc_xref.py"]),
    ("pr/docs", ["wc.md"]),
    ("mods", ["worlds-collide-presets.json"]),
]

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
  `mods/worlds-collide` submodule, untouched) on the ROM's data tables, then the kit's porter
  that turns its output into Pixel Remaster data.
- `kit/`: the [modding kit](https://github.com/thomasjteachey/ff6pr-kit) this mod builds on (a
  submodule): the porter, the build/package/deploy tools, the harnesses. `kit/pr/PORTING.md`
  is the project's status and index, `kit/pr/MODDING.md` the tooling guide.
- `pr/docs/wc.md`: this port's development log (what was built, tested and decided, by date).
- No ROM and no game files: `pr/tools/wc_gamedata.py` + the kit's `pr_bundle.py` make the copies
  the mod works from on the player's machine at first start.

## Build

Needs Windows, FF6 PR with BepInEx 6 and Magicite installed, .NET 6 SDK, Python 3.12, and the US
v1.0 SNES ROM (`rom/vanilla/Final Fantasy III (USA).sfc`, for WC's data tables; never shipped).

    git clone --recurse-submodules <this repo>
    cd pr && python -m venv .venv && .venv/Scripts/pip install -r requirements.txt
    .venv/Scripts/python ../kit/pr/tools/extract.py --game "<your FF6 PR folder>"   # pr/vanilla
    bash ../kit/pr/tools/deploy.sh --wc --pc                                       # build + install
    .venv/Scripts/python ../kit/pr/tools/package_mod.py wc                         # build/dist zip
    .venv/Scripts/python -m unittest tests.test_wc                                 # offline tests

The plugin builds against your game's BepInEx (`-p:GameDir=<folder>` if it isn't
`D:\\SteamLibrary\\steamapps\\common\\FINAL FANTASY VI PR`).

## Credits

Worlds Collide by AtmaTek and its contributors (MIT). BepInEx, Il2CppInterop, HarmonyX,
Magicite by Silvris. FINAL FANTASY VI is Square Enix's; this is an unofficial fan mod.
"""

GITIGNORE = R.GITIGNORE_COMMON + "pr/data/wc_base.bin\n"


def main():
    out = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else DEFAULT_OUT)
    entries = list(R.collect(PARTS))
    wc_commit = R.head(WC)
    kit_commit = R.head(R.KIT_REPO)
    version = "1.4.3"
    try:
        sys.path.insert(0, WC)
        import version as wcv  # noqa: E402  (WC's own version.py)
        version = wcv.__version__
    except Exception:
        pass
    texts = {"README.md": README.format(wc_version=version), ".gitignore": GITIGNORE,
             "pr/requirements.txt": R.REQUIREMENTS, "pr/modkit.json": R.manifest(keep=("wc",))}
    done = R.write(out, entries, texts, submodules=[("kit", R.KIT_URL, kit_commit),
                                                    ("mods/worlds-collide", WC_URL, wc_commit)])
    print(f"WC publish: {done} (WC {version}) -> {out}")


if __name__ == "__main__":
    main()
