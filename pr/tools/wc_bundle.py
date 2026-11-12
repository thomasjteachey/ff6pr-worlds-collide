"""Build what the Worlds Collide plugin needs besides its DLL: the seed generator (Python,
Worlds Collide, our porter, the PR data it reads, the ROM) and the baselines.

  .venv/Scripts/python tools/wc_bundle.py [OUT_DIR] [--check]   (default pr/build/WorldsCollide)

OUT_DIR/plugin/    -> BepInEx/plugins/WorldsCollide/ (deploy.sh adds the DLL + wc_flags.json)
  generator/python/      CPython, copied from this PC's install (the venv's base; no download):
                         runs on Windows and under Proton on the Steam Deck
  generator/tools/       the porter (pr/tools/*.py)
  generator/wc/          Worlds Collide, its own code as released
  generator/vanilla/     the PR data the porter reads (part of pr/vanilla)
  generator/data/wc_base.bin  the ROM's data tables WC reads (ROM-free since 2026-11-06)
  generator/gamedata.json     the game's files a player's install makes from their own game
                              (wc_gamedata.py setup; the zip leaves vanilla/, cache/,
                              baseline/files, baseline/vanilla_data and the Magicite folder out)
  generator/wc_presets.json
  generator/data/vwf.tbl the SNES menu font's text table (from BNW's source)
  baseline/files/        the vanilla copy of every file a seed can change (wc_live.managed_files)
  baseline/vanilla_data/ vanilla master tables + text a seed can change ("play without a seed")
OUT_DIR/magicite/WorldsCollide/ -> StreamingAssets/Magicite/WorldsCollide (wc_live.write_baseline)

--check: make a seed with the bundled Python alone (fresh environment) to prove it works.
"""
import fnmatch
import glob
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PR = os.path.dirname(HERE)
PROJECT = os.path.dirname(PR)
sys.path.insert(0, HERE)
import prdata as P  # noqa: E402
import wc_live as L  # noqa: E402

WC = os.path.join(PROJECT, "mods", "worlds-collide")
PRESETS = os.path.join(PROJECT, "mods", "worlds-collide-presets.json")

# the parts of pr/vanilla the porter reads (traced with an audit hook on WC seeds), whole
# directories so other flags reading other files there still work
VANILLA_DIRS = ["master", "message", "monster_ai", "battle_script", "battleextradata", "assetspath"]
VANILLA_GLOBS = ["map_*/**/entity_default.json"]
# not needed to run Python scripts that only use the standard library
LIB_SKIP = {"site-packages", "test", "tests", "tkinter", "idlelib", "turtledemo", "ensurepip", "lib2to3",
            "pydoc_data", "__pycache__", "venv", "unittest", "distutils"}
DLL_SKIP = ["_tkinter*", "tcl*", "tk*", "*.ico", "*.cat"]


def copytree(src, dst, skip_dirs=(), skip_files=()):
    for root, dirs, files in os.walk(src):
        dirs[:] = [d for d in dirs if d not in skip_dirs and not d.startswith(".")]
        rel = os.path.relpath(root, src)
        os.makedirs(os.path.join(dst, rel), exist_ok=True)
        for f in files:
            if any(fnmatch.fnmatch(f, p) for p in skip_files):
                continue
            shutil.copy2(os.path.join(root, f), os.path.join(dst, rel, f))


def size(path):
    return sum(os.path.getsize(os.path.join(r, f)) for r, _, fs in os.walk(path) for f in fs)


def python_runtime(dst):
    base = sys.base_prefix
    os.makedirs(dst, exist_ok=True)
    for f in os.listdir(base):
        if f.lower().endswith((".exe", ".dll")) and not f.lower().startswith(("tcl", "tk")):
            shutil.copy2(os.path.join(base, f), os.path.join(dst, f))
    copytree(os.path.join(base, "DLLs"), os.path.join(dst, "DLLs"), skip_files=DLL_SKIP)
    copytree(os.path.join(base, "Lib"), os.path.join(dst, "Lib"), skip_dirs=LIB_SKIP)
    # unittest is imported by nothing we run, but keep the module importable for safety
    copytree(os.path.join(base, "Lib", "unittest"), os.path.join(dst, "Lib", "unittest"), skip_dirs={"test", "__pycache__"})
    # the PSF license asks that its notice ships with any copy of Python
    shutil.copy2(os.path.join(base, "LICENSE.txt"), os.path.join(dst, "LICENSE.txt"))
    return f"Python {sys.version.split()[0]} from {base}"


def build(out):
    t0 = time.time()
    shutil.rmtree(out, ignore_errors=True)
    plugin = os.path.join(out, "plugin")
    gen = os.path.join(plugin, "generator")
    notes = [python_runtime(os.path.join(gen, "python"))]
    tools = os.path.join(gen, "tools")
    os.makedirs(tools)
    for f in glob.glob(os.path.join(HERE, "*.py")):
        shutil.copy2(f, tools)
    copytree(WC, os.path.join(gen, "wc"), skip_dirs={"__pycache__", ".git", ".github"}, skip_files=["*.sfc", "*.smc"])
    for d in VANILLA_DIRS:
        copytree(os.path.join(P.VANILLA, d), os.path.join(gen, "vanilla", d), skip_dirs={"__pycache__"})
    for pattern in VANILLA_GLOBS:
        for f in glob.glob(os.path.join(P.VANILLA, pattern), recursive=True):
            rel = os.path.relpath(f, P.VANILLA)
            os.makedirs(os.path.dirname(os.path.join(gen, "vanilla", rel)), exist_ok=True)
            shutil.copy2(f, os.path.join(gen, "vanilla", rel))
    # monster-in-a-box scripts (-cms rewrites their battles), and the SNES formation -> PR party
    # map (idmap.party reads every event script to break ties: precomputed here)
    import idmap
    import wc_live
    # every map file a seed can change, event layers too (ev_*.json: chests the PR keeps there,
    # check NPCs the seed re-skins): the generator edits its own copies (without them the bundle
    # ported fewer chests than the project did)
    # (the PR's empty chests: their layers come with chest_files, their scripts are the seed's to fill)
    for rel in wc_live.chest_files() + wc_live.npc_files() + wc_live.empty_chest_files():
        dst = os.path.join(gen, "vanilla", *rel.split("/"))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(os.path.join(P.VANILLA, *rel.split("/")), dst)
    for rel in wc_live.miab_files():
        os.makedirs(os.path.dirname(os.path.join(gen, "vanilla", rel)), exist_ok=True)
        shutil.copy2(os.path.join(P.VANILLA, *rel.split("/")), os.path.join(gen, "vanilla", rel))
    import port_chests   # the chest folders' sizes (its matcher's tie-break; the bundle has no tilemaps)
    with open(os.path.join(tools, port_chests.SIZES_FILE), "w", encoding="utf-8") as fh:
        json.dump(port_chests.chest_folder_sizes(), fh)
    with open(os.path.join(tools, idmap.PARTY_FILE), "w", encoding="utf-8") as fh:
        json.dump({str(f): idmap.party(f) for f in range(576)}, fh)
    shutil.copy2(PRESETS, os.path.join(gen, "wc_presets.json"))
    import bnw_text  # the menu font's text table (item/spell text decoding)
    os.makedirs(os.path.join(gen, "data"))
    shutil.copy2(bnw_text.VWF_TBL, os.path.join(gen, "data", "vwf.tbl"))
    # no SNES ROM in the bundle: the generator is ROM-free (wc_seed.py ROMFREE), WC's input the
    # ROM's data tables only (tools/wc_basedata.py), made here from the ROM on this machine
    import wc_basedata
    wc_basedata.build(os.path.join(gen, "data", "wc_base.bin"))
    notes.append("ROM-free (data/wc_base.bin)")

    # bytecode for everything the generator imports (its Python's library, WC, our tools),
    # compiled by that Python: without it each seed recompiles them all from source, seconds on
    # the PC and much longer under Wine on the Deck (copy2 kept the sources' times: the .pyc stay
    # valid where the bundle is installed)
    subprocess.run([os.path.join(gen, "python", "python.exe"), "-m", "compileall", "-q", "-j", "0",
                    os.path.join(gen, "python", "Lib"), os.path.join(gen, "wc"), os.path.join(gen, "tools")],
                   check=False, capture_output=True)
    notes.append("bytecode precompiled")
    # what every seed read from the vanilla files (the script index, the chests, the files a seed
    # can change), made once from the bundle's own vanilla folder (vcache.py: generator/cache)
    p = subprocess.run([os.path.join(gen, "python", "python.exe"), "-c",
                        "import pr_script, port_chests, wc_live; pr_script.index(); port_chests._scan_chests(); "
                        "print(len(wc_live.managed_files()))"],
                       cwd=tools, check=False, capture_output=True, text=True)
    notes.append(f"vanilla cache: {p.stdout.strip() or p.stderr.strip()[-200:]} managed files")
    magicite = os.path.join(out, "magicite", "WorldsCollide")
    base = os.path.join(plugin, "baseline")
    # WC's Track menu (every check, its gate, our done flag): WcTrack.cs
    import wc_events
    os.makedirs(base, exist_ok=True)
    track = wc_events.write_track(os.path.join(base, "wc_track.json"), WC)
    # the copies of the game's own files (the Magicite folder, baseline/files, baseline/vanilla_data;
    # generator/vanilla and cache above): made here for deploy, and on a player's machine from their
    # own game (wc_gamedata.py setup, from gamedata.json): the player zip leaves them out (PORTING.md
    # "WC without the game's files"). .complete marks this build's copies as made.
    import wc_gamedata
    wc_gamedata.write_baseline_data(plugin, magicite)
    manifest = wc_gamedata.manifest(gen)
    with open(os.path.join(gen, "vanilla", wc_gamedata.MARK), "w", encoding="utf-8") as f:
        f.write(manifest["id"])
    notes.append(f"gamedata.json: {len(manifest['files'])} game files for a player's setup")
    parts = {name: size(os.path.join(gen, name)) // (1 << 20) for name in os.listdir(gen) if os.path.isdir(os.path.join(gen, name))}
    # the mod is distributed: no SNES ROM may ship in it (PORTING.md; the user, 2026-11-06)
    roms = [os.path.join(r, f) for r, _, fs in os.walk(out) for f in fs if f.lower().endswith((".sfc", ".smc"))]
    if roms:
        raise SystemExit(f"bundle: SNES ROMs in the bundle, not shipping: {roms[:3]}")
    print(f"bundle: {notes[0]}; generator {size(gen) // (1 << 20)} MB {parts}; baseline {size(base) // 1024} KB; "
          f"Magicite folder {size(magicite) // 1024} KB; {time.time() - t0:.0f} s -> {out}")


def check(out):
    gen = os.path.join(out, "plugin", "generator")
    py = os.path.join(gen, "python", "python.exe")
    dest = os.path.join(out, "check_seed")
    env = {k: v for k, v in os.environ.items() if not k.startswith(("PYTHON", "VIRTUAL_ENV"))}
    env["PYTHONIOENCODING"] = "utf-8"
    t = time.time()
    p = subprocess.run([py, "-u", os.path.join(gen, "tools", "wc_seed.py"), "--seed", "bundlecheck", "--preset", "moogle cup",
                        "--out", dest], cwd=os.path.join(gen, "tools"), env=env, capture_output=True, text=True, encoding="utf-8")
    print(p.stdout.strip().splitlines()[-1] if p.stdout.strip() else p.stderr[-800:])
    ok = p.returncode == 0 and os.path.exists(os.path.join(dest, "wc_seed.json"))
    if ok and os.path.exists(P.VANILLA_ROM):
        # the bundle's seed = the project's (ROM-free both): a file the bundle lacks would show
        # (the bundle once carried no event-layer map files, so its seeds left those chests out)
        import wc_parity
        mine = os.path.join(out, "check_seed_project")
        q = subprocess.run([sys.executable, os.path.join(HERE, "wc_seed.py"), "--seed", "bundlecheck", "--preset", "moogle cup",
                            "--out", mine], cwd=PR, env=dict(os.environ, FF6_WC_ROMFREE="1", PYTHONIOENCODING="utf-8"),
                           capture_output=True, text=True, encoding="utf-8")
        diffs = wc_parity.compare(mine, dest) if q.returncode == 0 else [f"the project's generator failed: {q.stderr[-300:]}"]
        print("bundle parity: " + ("the project's seed folder" if not diffs else "; ".join(diffs[:8])))
        ok = not diffs
        shutil.rmtree(mine, ignore_errors=True)
    print(f"bundle check: {'ok' if ok else 'FAILED'} ({time.time() - t:.1f} s)")
    shutil.rmtree(dest, ignore_errors=True)
    shutil.rmtree(os.path.join(gen, "mods"), ignore_errors=True)
    for root, dirs, _ in os.walk(gen):  # the check's bytecode caches stay out of the bundle
        for d in [d for d in dirs if d == "__pycache__"]:
            shutil.rmtree(os.path.join(root, d), ignore_errors=True)
    return ok


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    out = os.path.abspath(args[0]) if args else os.path.join(PR, "build", "WorldsCollide")
    build(out)
    if "--check" in sys.argv and not check(out):
        sys.exit(1)
