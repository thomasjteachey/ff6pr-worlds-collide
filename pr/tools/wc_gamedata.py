"""Worlds Collide's copies of the game's own files, made on the player's machine from their own
copy of the game. The mod is distributed, so it doesn't ship the Pixel Remaster's files (the same
rule as the SNES ROM; PORTING.md "WC without the game's files").

    python tools/wc_gamedata.py setup [--game DIR] [--magicite DIR]    the player's machine
    python tools/wc_gamedata.py status                                  exit 0: set up for this manifest
    python tools/wc_gamedata.py manifest <generator dir>                wc_bundle.py, on this PC

wc_bundle.py writes generator/gamedata.json (manifest()): every PR file the generator reads
(generator/vanilla) and every one that making the plugin's baseline reads, each with its sha1.
setup (WcPlugin runs it with the bundled Python when generator/vanilla/.complete isn't the
manifest's id) reads those files out of the game's asset bundles (pr_bundle.py, standard
library only) and makes what the bundle used to ship:

    generator/vanilla/          the generator's PR files
    generator/cache/            vcache: the script index, the chests, the files a seed may change
    baseline/files/             the vanilla copy of every file a seed may change
    baseline/vanilla_data/      vanilla master tables and text ("play without a seed")
    <Magicite>/WorldsCollide/   the mod's Magicite folder (wc_live.write_baseline)

then writes generator/vanilla/.complete (the manifest's id) last. Magicite needs its folder's
files at startup: a game that started before setup ran needs one restart.
Layout: this file is generator/tools/ (in the bundle) or pr/tools/ (in the project).
"""
import argparse
import glob
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
GEN = os.path.dirname(HERE)                 # generator/ (in the project: pr/)
PLUGIN = os.path.dirname(GEN)               # BepInEx/plugins/WorldsCollide
MANIFEST = "gamedata.json"
MARK = ".complete"
BUNDLES = os.path.join("StreamingAssets", "aa", "StandaloneWindows64")
MAGICITE_CFG = os.path.join("BepInEx", "config", "silvris.magicite.cfg")
FOLDER = "WorldsCollide"


def _sha1(data):
    return hashlib.sha1(data).hexdigest()


# --- what the plugin needs made from the PR's files (wc_bundle.py on this PC, setup on a player's)

def write_baseline_data(plugin, magicite):
    """The Magicite folder, baseline/files and baseline/vanilla_data, from prdata.VANILLA."""
    import prdata as P
    import wc_events
    import wc_live as L
    L.write_baseline(magicite)
    base = os.path.join(plugin, "baseline")
    for d in ("files", "vanilla_data"):
        shutil.rmtree(os.path.join(base, d), ignore_errors=True)
    for rel in L.managed_files():
        dst = os.path.join(base, "files", *rel.split("/"))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(os.path.join(magicite, *rel.split("/")), dst)
    for t in L.MANAGED_TABLES:
        table, _ = L.full_table(t)
        dst = os.path.join(base, "vanilla_data", *L.MASTER_PATH.split("/"), t + ".csv")
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        with open(dst, "w", encoding="utf-8", newline="") as f:
            f.write(table.to_csv(table.rows.keys()))
    for m in L.MESSAGE_FILES:
        text, _ = L.full_messages(None, m)
        dst = os.path.join(base, "vanilla_data", *L.MESSAGE_PATH.split("/"), m + ".txt")
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        story = os.path.join(base, "vanilla_data", *L.MESSAGE_PATH.split("/"), "story_mes_en.txt")
        van_story = P.messages("story_mes", "en")
        with open(story, "w", encoding="utf-8", newline="") as f:   # the Zozo clues' vanilla lines
            for k in wc_events.ZOZO_CLUES.values():
                f.write(f"{k}\t{van_story.get(k, '')}\r\n")
        with open(dst, "w", encoding="utf-8", newline="") as f:
            for k, v in text.items():
                f.write(f"{k}\t{v}\r\n")


def write_cache():
    """generator/cache (vcache), from prdata.VANILLA: what every seed would read otherwise."""
    import port_chests
    import pr_script
    import wc_live
    pr_script.index()
    port_chests._scan_chests()
    return len(wc_live.managed_files())


def _run(code, vanilla=None, cwd=HERE):
    """Python code in a fresh process of this Python (its own module caches), prdata.VANILLA set."""
    pre = "import sys; sys.path.insert(0, %r); import prdata; " % HERE
    if vanilla:
        pre += "prdata.VANILLA = %r; " % vanilla
    env = {k: v for k, v in os.environ.items() if not k.startswith(("PYTHON", "VIRTUAL_ENV"))}
    env["PYTHONIOENCODING"] = "utf-8"
    p = subprocess.run([sys.executable, "-c", pre + code], cwd=cwd, env=env, capture_output=True,
                       text=True, encoding="utf-8")
    if p.returncode != 0:
        raise SystemExit(f"setup: {code.split(';')[-1].strip()[:80]} failed:\n{p.stderr[-1500:]}")
    return p.stdout


# --- the manifest (this PC: pr/vanilla is tools/extract.py's extract of the game)

def _trace_baseline_reads():
    """Every pr/vanilla file making the baseline reads, from a fresh process with no caches."""
    import tempfile
    out = tempfile.mkdtemp(prefix="wc_gamedata_")
    code = (
        "import os, json, vcache\n"
        "van = os.path.normcase(os.path.abspath(prdata.VANILLA))\n"
        "seen = set()\n"
        "def hook(ev, a):\n"
        "    if ev == 'open' and isinstance(a[0], str):\n"
        "        p = os.path.abspath(a[0])\n"
        "        if os.path.normcase(p).startswith(van + os.sep): seen.add(os.path.relpath(p, prdata.VANILLA))\n"
        "sys.addaudithook(hook)\n"
        "vcache.cached = lambda name, compute: compute()\n"
        "import wc_gamedata\n"
        f"wc_gamedata.write_baseline_data({os.path.join(out, 'plugin')!r}, {os.path.join(out, 'magicite')!r})\n"
        "print(json.dumps(sorted(seen)))\n")
    try:
        lines = _run(code).strip().splitlines()
        return json.loads(lines[-1])
    finally:
        shutil.rmtree(out, ignore_errors=True)


def manifest(gen):
    """gen/gamedata.json: the generator's PR files (gen/vanilla) and the baseline's, with sha1s."""
    import prdata as P
    van = os.path.join(gen, "vanilla")
    files = {}
    for root, _, fs in os.walk(van):
        for f in fs:
            if f == MARK:
                continue
            p = os.path.join(root, f)
            with open(p, "rb") as fh:
                files[os.path.relpath(p, van).replace("\\", "/")] = [_sha1(fh.read()), 1]
    # the names the baseline opened, in the files' own case (Windows doesn't care, the bundles do)
    actual = {}
    traced = _trace_baseline_reads()
    for group in sorted({r.replace("\\", "/").split("/")[0] for r in traced}):
        for root, _, fs in os.walk(os.path.join(P.VANILLA, group)):
            for f in fs:
                rel = os.path.relpath(os.path.join(root, f), P.VANILLA).replace("\\", "/")
                actual[rel.lower()] = rel
    for r in traced:
        rel = actual[r.replace("\\", "/").lower()]
        if rel not in files:
            with open(os.path.join(P.VANILLA, *rel.split("/")), "rb") as fh:
                files[rel] = [_sha1(fh.read()), 0]
    doc = {"_doc": "WorldsCollide: the game's files setup reads from the player's game (tools/wc_gamedata.py); "
                   "[sha1, 1 = generator/vanilla too]",
           "id": _sha1(json.dumps(sorted(files.items())).encode())[:16], "files": dict(sorted(files.items()))}
    with open(os.path.join(gen, MANIFEST), "w", encoding="utf-8", newline="\n") as f:   # a file a line
        f.write('{"_doc": %s,\n"id": "%s",\n"files": {\n' % (json.dumps(doc["_doc"]), doc["id"]))
        f.write(",\n".join(f"{json.dumps(k)}: {json.dumps(v)}" for k, v in doc["files"].items()))
        f.write("\n}}\n")
    return doc


# --- the player's machine

def find_game(start):
    """The game's folder (the one with <Game>_Data/StreamingAssets/aa) at or above `start`."""
    d = os.path.abspath(start)
    while True:
        for data in glob.glob(os.path.join(d, "*_Data")):
            if os.path.isdir(os.path.join(data, BUNDLES)):
                return d, data
        up = os.path.dirname(d)
        if up == d:
            return None, None
        d = up


def magicite_dir(game, data):
    """Magicite's import directory (BepInEx/config/silvris.magicite.cfg, as plugin/Shared/MagiciteDir.cs)."""
    value = "%StreamingAssets%/Magicite"
    try:
        with open(os.path.join(game, MAGICITE_CFG), encoding="utf-8") as f:
            for line in f:
                t = line.strip()
                if not t.startswith("#") and t.lower().startswith("import directory") and "=" in t:
                    value = t.split("=", 1)[1].strip()
    except OSError:
        pass
    value = value.replace("%StreamingAssets%", os.path.join(data, "StreamingAssets")).replace("%DataPath%", data)
    return os.path.join(os.path.normpath(value), FOLDER)


def extract(doc, bundles, out):
    """The manifest's files from the game's bundles into out/ -> (files, differing, missing)."""
    by_group = {}
    for rel, (sha, _) in doc["files"].items():
        group, rest = rel.split("/", 1)
        by_group.setdefault(group, []).append((rest, sha))
    sys.path.insert(0, HERE)
    import pr_bundle
    differ, missing, n = [], [], 0
    for group, wanted in sorted(by_group.items()):
        found = sorted(glob.glob(os.path.join(bundles, glob.escape(group) + "_assets_all_*.bundle")))
        if not found:
            missing += [f"{group}/{r}" for r, _ in wanted]
            continue
        assets = dict(pr_bundle.text_assets(found[-1]))
        for rest, sha in wanted:
            if rest == "keys/Export.json":      # tools/extract.py's key list, made the same way
                keys = {}
                for k in assets:
                    stem = os.path.splitext(k)[0]
                    keys[os.path.basename(stem)] = stem
                text = json.dumps({"keys": list(keys), "values": list(keys.values())}, indent=2)
                data = text.replace("\n", "\r\n").encode("utf-8")     # extract.py ran on Windows
            elif rest in assets:
                data = assets[rest]
            else:
                missing.append(f"{group}/{rest}")
                continue
            if _sha1(data) != sha:
                differ.append(f"{group}/{rest}")
            dst = os.path.join(out, group, *rest.split("/"))
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            with open(dst, "wb") as f:
                f.write(data)
            n += 1
    return n, differ, missing


def setup(game=None, magicite=None, plugin=PLUGIN, gen=GEN):
    """Makes the game data; True when every file matched the manifest's (a changed game: False)."""
    t0 = time.time()
    with open(os.path.join(gen, MANIFEST), encoding="utf-8") as f:
        doc = json.load(f)
    root, data = find_game(game or plugin)
    if not root:
        raise SystemExit(f"setup: no game folder (with *_Data/{BUNDLES.replace(os.sep, '/')}) at or above {game or plugin}")
    magicite = magicite or magicite_dir(root, data)
    work = os.path.join(gen, "setup_work")
    shutil.rmtree(work, ignore_errors=True)
    full = os.path.join(work, "vanilla")
    n, differ, missing = extract(doc, os.path.join(data, BUNDLES), full)
    if missing:
        shutil.rmtree(work, ignore_errors=True)
        raise SystemExit(f"setup: {len(missing)} of the game's files aren't in its bundles "
                         f"(a different game version?): {', '.join(missing[:5])}")
    van = os.path.join(gen, "vanilla")
    shutil.rmtree(van, ignore_errors=True)
    for rel, (_, gen_too) in doc["files"].items():
        if gen_too:
            dst = os.path.join(van, *rel.split("/"))
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(os.path.join(full, *rel.split("/")), dst)
    t1 = time.time()
    # the baseline from every file it reads, the cache from the generator's own folder: each in
    # a fresh process (prdata.VANILLA differs, and the porter's modules cache what they read).
    # The Magicite folder is made beside its place and moved in last: Magicite never sees half of one.
    made = os.path.join(work, "magicite")
    _run(f"import wc_gamedata; wc_gamedata.write_baseline_data({plugin!r}, {made!r})", vanilla=full)
    t2 = time.time()
    shutil.rmtree(os.path.join(gen, "cache"), ignore_errors=True)
    managed = _run("import wc_gamedata; print(wc_gamedata.write_cache())", vanilla=van).strip()
    shutil.rmtree(magicite, ignore_errors=True)
    os.makedirs(os.path.dirname(magicite), exist_ok=True)
    shutil.move(made, magicite)
    shutil.rmtree(work, ignore_errors=True)
    with open(os.path.join(van, MARK), "w", encoding="utf-8") as f:
        f.write(doc["id"])
    note = f"; {len(differ)} differ from the game this mod was made with: {', '.join(differ[:3])}" if differ else ""
    print(f"setup: {n} files from the game's bundles ({t1 - t0:.0f} s), baseline ({t2 - t1:.0f} s), cache "
          f"({managed} managed files, {time.time() - t2:.0f} s) -> {magicite}{note}")
    return not differ


LEFT_OUT = ["generator/vanilla", "generator/cache", "baseline/files", "baseline/vanilla_data"]   # = modkit.json


def selftest(build, game, work):
    """A player's install from the build (plugin folder without LEFT_OUT, no Magicite folder), set up
    with its own Python from the game's bundles: the same files as the build's? (this PC)"""
    plugin = os.path.join(work, "game", "BepInEx", "plugins", FOLDER)
    shutil.rmtree(work, ignore_errors=True)
    skip = {os.path.normcase(os.path.join(build, "plugin", *p.split("/"))) for p in LEFT_OUT}
    shutil.copytree(os.path.join(build, "plugin"), plugin,
                    ignore=lambda d, names: [n for n in names if os.path.normcase(os.path.join(d, n)) in skip])
    magicite = os.path.join(work, "magicite", FOLDER)
    py = os.path.join(plugin, "generator", "python", "python.exe")
    env = {k: v for k, v in os.environ.items() if not k.startswith(("PYTHON", "VIRTUAL_ENV"))}
    env["PYTHONIOENCODING"] = "utf-8"
    p = subprocess.run([py, os.path.join(plugin, "generator", "tools", "wc_gamedata.py"), "setup", "--game", game,
                        "--magicite", magicite], cwd=os.path.join(plugin, "generator", "tools"), env=env,
                       capture_output=True, text=True, encoding="utf-8")
    print((p.stdout.strip().splitlines() or [""])[-1] if p.returncode == 0 else p.stdout[-500:] + p.stderr[-1500:])
    if p.returncode != 0:
        return False

    def files(d):
        return {os.path.relpath(os.path.join(r, f), d).replace("\\", "/") for r, _, fs in os.walk(d) for f in fs}

    bad = []
    for mine, theirs, label in [(os.path.join(plugin, *p.split("/")), os.path.join(build, "plugin", *p.split("/")), p)
                                for p in LEFT_OUT] + [(magicite, os.path.join(build, "magicite", FOLDER), "Magicite")]:
        a, b = files(mine) - {MARK}, files(theirs) - {MARK}
        if a != b:
            bad.append(f"{label}: {len(a - b)} only made, {len(b - a)} only in the build")
        for rel in sorted(a & b):
            with open(os.path.join(mine, rel), "rb") as f:
                x = f.read()
            with open(os.path.join(theirs, rel), "rb") as f:
                y = f.read()
            if x != y and label == "generator/cache" and json.loads(x)["value"] == json.loads(y)["value"]:
                continue        # the cache's stamp has the files' times in it
            if x != y:
                bad.append(f"{label}/{rel} differs")
    print("setup self-test: " + ("the build's files, every one" if not bad else "; ".join(bad[:8])))
    if not bad:
        shutil.rmtree(work, ignore_errors=True)
    return not bad


def status(gen=GEN):
    try:
        with open(os.path.join(gen, MANIFEST), encoding="utf-8") as f:
            want = json.load(f)["id"]
        with open(os.path.join(gen, "vanilla", MARK), encoding="utf-8") as f:
            return f.read().strip() == want
    except (OSError, ValueError, KeyError):
        return False


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("command", choices=["setup", "status", "manifest", "selftest"])
    ap.add_argument("dir", nargs="?", help="manifest: the generator folder; selftest: the build (pr/build/WorldsCollide)")
    ap.add_argument("--game", help="the game's folder (default: the one this plugin is in)")
    ap.add_argument("--magicite", help="the mod's Magicite folder (default: Magicite's import directory/WorldsCollide)")
    a = ap.parse_args()
    if a.command == "status":
        ok = status()
        print("set up" if ok else "not set up")
        return 0 if ok else 1
    if a.command == "manifest":
        doc = manifest(os.path.abspath(a.dir))
        print(f"{MANIFEST}: {len(doc['files'])} files, id {doc['id']}")
        return 0
    if a.command == "selftest":
        sys.path.insert(0, HERE)
        from prkit import games
        build = os.path.abspath(a.dir or os.path.join(GEN, "build", "WorldsCollide"))
        work = os.path.join(os.path.dirname(build), "WorldsCollide-setup-test")
        return 0 if selftest(build, a.game or games.get("ff6").install, work) else 1
    setup(a.game, a.magicite)
    return 0


if __name__ == "__main__":
    sys.exit(main())
