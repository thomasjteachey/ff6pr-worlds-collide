"""prkit: tooling shared by every Pixel Remaster mod in this project (FF1-FF6 PR).

    games      which PR games exist, where Steam put them, every path a tool needs
    data       read a game's extracted data (master CSV tables, message text)
    magicite   write and check Magicite mod folders
    mods       the mod manifest (pr/modkit.json): every mod, its game and its parts
    package    a player zip for any mod in the manifest
    deploy     build + install a mod on this PC (and the Steam Deck)
    bepinex    put BepInEx + Magicite into another PR game
    scaffold   start a new mod (plugin, data script, test, manifest entry)

Command line: .venv/Scripts/python tools/kit.py --help   (pr/MODDING.md has the guide)

Only developer tools import it (kit.py, package_mod.py, extract.py --game-id). The porter and the
Worlds Collide generator (prdata.py, port*.py, wc_*.py) never do, on purpose: the WC generator
bundle copies tools/*.py but not sub-folders into the player's game and must keep working there.
"""
import os

TOOLS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PR = os.path.dirname(TOOLS)
PROJECT = os.path.dirname(PR)
