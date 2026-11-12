# Worlds Collide for FINAL FANTASY VI Pixel Remaster

[Worlds Collide](https://github.com/ff6wc/WorldsCollide) 1.4.3, the FF6 open-world randomizer,
played in the Steam Pixel Remaster: make seeds in the game with every option of the website
(presets, pasted flag strings, a seed field), play them, switch between them; saves remember
their seed. Seeds match the website's for the same seed and flags.

## Play

Download `WorldsCollide-PR.zip` from the [latest release](../../releases/latest) and follow its
README: BepInEx 6 (IL2CPP, bleeding edge) and Magicite first, then extract the zip into the
game folder. Steam Deck / Linux: add `WINEDLLOVERRIDES="winhttp=n,b" %command%` to the game's
launch options. The first start takes a minute or two longer: the mod makes the game data it
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
`D:\SteamLibrary\steamapps\common\FINAL FANTASY VI PR`).

## Credits

Worlds Collide by AtmaTek and its contributors (MIT). BepInEx, Il2CppInterop, HarmonyX,
Magicite by Silvris. FINAL FANTASY VI is Square Enix's; this is an unofficial fan mod.
