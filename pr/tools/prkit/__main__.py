"""tools/kit.py: the project's tooling for every Pixel Remaster (pr/MODDING.md is the guide).

  games [--write-props]       the six PRs: installed where, BepInEx/interop/Magicite, mods in them
  mods                        the mods in pr/modkit.json and whether they're built
  setup <game>                BepInEx + Magicite into another PR, copied from FF6's install
  extract <game> [--audio]    the game's data (tables, text, scripts) -> its vanilla folder
  api <game>                  every type/field/method of the game's code -> its API dump
  new-mod <Name> --game <g>   a new mod: plugin, feature, data script, test, manifest entry
  check <mod>                 its parts are built and its Magicite folder loads
  package <mod>               the player zip (build/dist)
  deploy <mod> [--pc|--deck]  build + install on this PC and the Steam Deck
  run <game> [--harness]      the game headless for a test run (only with the user's OK)
"""
import argparse
import os
import subprocess
import sys

from . import PR, TOOLS, bepinex, data, deploy, games, headless, magicite, mods, package, scaffold


def cmd_games(a):
    for g in games.all_games():
        if not g.installed:
            print(f"{g.id}  {g.title:<18} not installed (Steam app {g.appid})")
            continue
        s = g.status()
        flags = ", ".join(f"{k} {'yes' if s[k] else 'no'}" for k in ("bepinex", "interop", "magicite"))
        print(f"{g.id}  {g.title:<18} {g.install}")
        print(f"     {flags}; plugins: {', '.join(s['plugins']) or 'none'}; Magicite mods: {', '.join(s['magicite_mods']) or 'none'}")
        print(f"     data extracted: {'yes' if data.GameData(g).extracted else 'no'} ({g.vanilla})")
    if a.write_props:
        print(f"wrote {games.write_props()}")


def cmd_mods(a):
    for m in mods.all_mods_list():
        missing = m.missing()
        state = "built" if not missing else "not built: " + ", ".join(missing)
        extra = f"; own deploy: {' '.join(m.deploy)}" if m.deploy else ""
        print(f"{m.id:<10} {m.name:<16} {m.game_id}  {state}{extra}")


def cmd_extract(a):
    g = games.get(a.game)
    cmd = [sys.executable, os.path.join(TOOLS, "extract.py"), "--game-id", g.id]
    if a.audio:
        cmd.append("--audio")
    return subprocess.run(cmd).returncode


def cmd_api(a):
    g = games.get(a.game)
    dll = os.path.join(g.bepinex, "interop", "Assembly-CSharp.dll")
    if not os.path.exists(dll):
        print(f"no {dll}: BepInEx makes it at the game's first start with BepInEx (kit.py setup {g.id})")
        return 1
    os.makedirs(os.path.dirname(g.api_dump), exist_ok=True)
    r = subprocess.run(["dotnet", "run", "-c", "Release", "--project", os.path.join(TOOLS, "TypeDump"), "--", dll, g.api_dump])
    return r.returncode


def cmd_check(a):
    m = mods.get(a.mod)
    bad = 0
    missing = m.missing()
    if missing:
        print("not built: " + ", ".join(missing))
        bad += 1
    if m.magicite and os.path.isdir(m.path(m.magicite)):
        gd = data.GameData(m.game_id)
        problems, notes = magicite.validate(m.path(m.magicite), gd if gd.extracted else None)
        for p in problems:
            print("PROBLEM", p)
        print(f"{m.magicite}: {len(problems)} problems, {len(notes)} notes" + ("" if gd.extracted else
              f" (the game's data isn't extracted: kit.py extract {m.game_id} for the full check)"))
        if a.notes:
            for n in notes:
                print("note", n)
        bad += len(problems)
    return 1 if bad else 0


def cmd_package(a):
    try:
        path, n = package.package(a.mod, a.out)
    except FileNotFoundError as e:
        print(e)
        return 1
    print(f"{path}: {n} files ({os.path.getsize(path) // 1024} KB)")
    return 0


def cmd_deploy(a):
    pc, deck = not a.deck, not a.pc
    return deploy.deploy(a.mod, pc=pc, deck=deck, full=a.full, build=not a.no_build, dry_run=a.dry_run)


def cmd_setup(a):
    return bepinex.setup(a.game, a.source, dry_run=a.dry_run, force=a.force)


def cmd_new_mod(a):
    scaffold.new_mod(a.name, a.game, title=a.title, mod_id=a.id, with_data=not a.no_data, with_plugin=not a.no_plugin)
    return 0


def cmd_run(a):
    settings = {}
    for kv in a.set or []:
        k, _, v = kv.partition("=")
        settings[k.strip()] = v.strip() or "1"
    return headless.run(a.game, harness=a.harness, minutes=a.minutes, until=a.until, settings=settings, dry_run=a.dry_run)


def main(argv=None):
    ap = argparse.ArgumentParser(prog="kit.py", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("games")
    p.add_argument("--write-props", action="store_true", help="write plugin/PrKit/Games.props (game folders for builds)")
    p.set_defaults(fn=cmd_games)
    sub.add_parser("mods").set_defaults(fn=cmd_mods)
    p = sub.add_parser("extract")
    p.add_argument("game")
    p.add_argument("--audio", action="store_true")
    p.set_defaults(fn=cmd_extract)
    p = sub.add_parser("api")
    p.add_argument("game")
    p.set_defaults(fn=cmd_api)
    p = sub.add_parser("setup")
    p.add_argument("game")
    p.add_argument("--from", dest="source", default="ff6")
    p.add_argument("--force", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    p.set_defaults(fn=cmd_setup)
    p = sub.add_parser("new-mod")
    p.add_argument("name", help="folder/assembly/namespace name, e.g. FiveJobFiesta")
    p.add_argument("--game", required=True)
    p.add_argument("--title")
    p.add_argument("--id")
    p.add_argument("--no-data", action="store_true", help="a plugin only (no Magicite data)")
    p.add_argument("--no-plugin", action="store_true", help="Magicite data only")
    p.set_defaults(fn=cmd_new_mod)
    p = sub.add_parser("check")
    p.add_argument("mod")
    p.add_argument("--notes", action="store_true")
    p.set_defaults(fn=cmd_check)
    p = sub.add_parser("package")
    p.add_argument("mod")
    p.add_argument("--out")
    p.set_defaults(fn=cmd_package)
    p = sub.add_parser("deploy")
    p.add_argument("mod")
    g = p.add_mutually_exclusive_group()
    g.add_argument("--pc", action="store_true", help="this PC only")
    g.add_argument("--deck", action="store_true", help="only push the PC's install to the Deck")
    p.add_argument("--full", action="store_true", help="also resend BepInEx itself to the Deck")
    p.add_argument("--no-build", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    p.set_defaults(fn=cmd_deploy)
    p = sub.add_parser("run")
    p.add_argument("game")
    p.add_argument("--harness", action="store_true", help="listen for tools/game_harness.py commands")
    p.add_argument("--minutes", type=int, default=30)
    p.add_argument("--until", help="stop when the log matches this regex")
    p.add_argument("--set", action="append", metavar="KEY=VALUE", help="more request lines for the mod")
    p.add_argument("--dry-run", action="store_true")
    p.set_defaults(fn=cmd_run)
    a = ap.parse_args(argv)
    try:
        return a.fn(a) or 0
    except (KeyError, ValueError, FileNotFoundError, FileExistsError, RuntimeError) as e:
        print(f"{a.cmd}: {e}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
