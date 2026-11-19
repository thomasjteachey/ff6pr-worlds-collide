"""The public Battle Options repository: the mod's plugin and the kit it compiles in, in their own git tree.

    .venv/Scripts/python tools/bo_publish.py [OUT]     (default: ../../ffpr-battle-options)

Copies pr/plugin/BattleOptionsPlugin and pr/plugin/PrKit (the kit's C#, compiled into the plugin)
into OUT with the project's layout (plugin/...), and writes its README and .gitignore. Never the
game's files, build output, or this PC's paths (PrKit/Games.props, written by kit.py games, stays
here: a build elsewhere passes -p:GameDir=...). OUT's .git is left alone: commit and push from there.
"""
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PR = os.path.dirname(HERE)
PROJECT = os.path.dirname(PR)
DEFAULT_OUT = os.path.join(os.path.dirname(PROJECT), "ffpr-battle-options")

PARTS = ["plugin/BattleOptionsPlugin", "plugin/PrKit"]
SKIP_DIRS = {"bin", "obj", ".vs"}
SKIP_FILES = {"Games.props"}

README = """# Battle Options for the FINAL FANTASY Pixel Remaster

Three battle settings in the game's own **Config** menu, for FINAL FANTASY VI Pixel Remaster
(Steam). An add-on: it changes no game data and installs beside the unmodded game or other mods
(Brave New World, Worlds Collide, HD-2D).

| Setting | Choices | What it does |
|---|---|---|
| **Battle Mode** | Wait / Active / **Pause** | Pause is Wait, and all of the battle's time also stops while you choose a command and while an action plays out: every ATB gauge (your party's and the enemies'), every chosen action's cast time and every status timer (Regen, Poison, Stop, Doom...). |
| **Cast Bars** | Off / On | While a character waits between a chosen command and its action (a spell's cast, an attack's wind-up, Jump's time in the air), their ATB gauge shows the wait left, draining. |
| **Enemy ATB** | Off / On | Each enemy's ATB gauge is shown under it (gone once the enemy is defeated or escapes). |

The Cast Bars and Enemy ATB rows sit under the battle settings (after Battle Messages). The choices
are kept in `BepInEx/config/prmod.battleoptions.cfg`; under Pause the game's own config keeps Wait,
so removing the mod leaves the game on Wait.

## Install

1. BepInEx 6 for Unity IL2CPP games, x64, in the game folder (a bleeding-edge build from
   builds.bepinex.dev; made with 6.0.0-be.697). Start the game once so BepInEx sets itself up.
2. Put the `BattleOptions` folder (with `BattleOptions.PR.dll`) in `BepInEx/plugins`.

Steam Deck / Linux (Proton): BepInEx only starts with this in the game's Steam launch options:

    WINEDLLOVERRIDES="winhttp=n,b" %command%

Remove it by deleting `BepInEx/plugins/BattleOptions`.

## Compatibility

- **FINAL FANTASY VI PR**: checked headless with Worlds Collide installed (the rows in the in-game
  Config's battle page; Pause holding gauges while turns are up; a Fight's wait drained on
  the cast bar; saves untouched). Pause and the cast bars apply after Worlds Collide's SNES ATB rates
  and command waits; Worlds Collide 0.6.4's status clock waits with Pause. Built to sit beside Brave New World too (its own Wait Gauge: the Cast Bars
  setting decides whether it shows), not yet run together.
- **FINAL FANTASY IV and V PR**: the mod names only the engine code the Pixel Remasters share and
  finds each game's own Config and ATB classes by name, so it loads there, but it hasn't been tested
  in those games. FF V has no Cast Bars row (every command acts at once there); FF IV's cast bars
  measure the game's own countdown, since it has no separate wait-time table.
- FINAL FANTASY I-III PR have no ATB: the mod stays off there.

## Build

    cd plugin/BattleOptionsPlugin
    dotnet build -c Release -p:GameDir="<the game's folder>"

The game folder needs BepInEx with its generated `BepInEx/interop` (the game's code as .NET
assemblies: start the game once with BepInEx). `plugin/PrKit` is the modding kit the plugin compiles
in (safe Harmony hooks, switchable features, headless test runs); `PrKit.props` explains the setup.

## How it works

- `ConfigMenu.cs`: the Config menu's rows are `ConfigCommandController`s whose `ConfigCommandsData`
  names their setting. Battle Mode gets a third choice; Cast Bars and Enemy ATB are copies of an
  On/Off row with setting types of their own, handled by hooks on the game's row set-up, Left/Right
  and help-line methods.
- `PauseAtb.cs`: time is held while the game's queue of characters waiting for a command
  (`BattleProgressATB.battleATBOrderUiStack`) isn't empty or an action is being prepared, played or
  finished (`BattleActExection.nowState`): every gauge increase (`ATBCalc`) is zero, the cast
  countdown (`UpdateChantTime`) and the status timers (`BattleConditionController.Update`, the part
  of the ATB update the game's own Wait skips in its menus) are skipped. A mod with a battle clock
  of its own can wait too: the held frame is in AppDomain data `pr.battle.timeHeldFrame` (Worlds
  Collide's SNES status clock reads it).
- `CastBars.cs`: the wait left on the character's chant (`BattleProgressATB.chantingInfo`) drawn on
  their ATB gauge (`BattlePlayerInfomationController.UpdateATB`).
- `EnemyAtb.cs`: each enemy's gauge (`gaugeStatusDictionary`) drawn under its sprite, projected by
  the camera that draws it.

No game files are in this repository.
"""

GITIGNORE = """bin/
obj/
.vs/
*.user
plugin/PrKit/Games.props
"""


def copy_tree(src, dst):
    n = 0
    for d, dirs, files in os.walk(src):
        dirs[:] = [x for x in dirs if x not in SKIP_DIRS]
        rel = os.path.relpath(d, src)
        for f in files:
            if f in SKIP_FILES:
                continue
            target = os.path.join(dst, rel, f)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            shutil.copy2(os.path.join(d, f), target)
            n += 1
    return n


def main():
    out = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_OUT
    os.makedirs(out, exist_ok=True)
    for part in PARTS:
        dst = os.path.join(out, part)
        if os.path.isdir(dst):
            shutil.rmtree(dst)
        n = copy_tree(os.path.join(PR, part), dst)
        print(f"[publish] {part}: {n} files")
    with open(os.path.join(out, "README.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(README)
    with open(os.path.join(out, ".gitignore"), "w", encoding="utf-8", newline="\n") as f:
        f.write(GITIGNORE)
    print(f"[publish] {out}: README.md, .gitignore written; commit and push from there")
    return 0


if __name__ == "__main__":
    sys.exit(main())
