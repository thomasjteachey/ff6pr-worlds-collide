"""Worlds Collide's copies of the game's own files, made on the player's machine from their own
copy of the game. The mod is distributed, so it doesn't ship the Pixel Remaster's files (the same
rule as the SNES ROM; PORTING.md "WC without the game's files").

    python tools/wc_gamedata.py setup [--game DIR] [--magicite DIR]    the player's machine
    python tools/wc_gamedata.py status                                  exit 0: set up for this manifest
    python tools/wc_gamedata.py recipe <build dir>                      wc_bundle.py, on this PC
    python tools/wc_gamedata.py selftest [<build dir>]                  this PC: setup = the build?

What a player's install needs is the same for everyone (every copy of the game has the same
files), so wc_bundle.py works it out once (make_recipe) and ships only the recipe:

    generator/gamedata.json         the game files setup reads (sha1 each): the generator's
                                    vanilla folder and every recipe's source
    generator/gamedata_recipe.json  each made file: a copy of a game file, a game monster AI
                                    script under a slot's name, a game file with WC's changes (a
                                    line patch of its JSON or text: only the changed lines), or one
                                    of ours (shipped in generator/gamedata/: key lists, the cache)

setup (WcPlugin runs it with the bundled Python when generator/vanilla/.complete isn't the
manifest's id) reads those files out of the game's asset bundles (pr_bundle.py, standard library
only), writes them and the recipe's files, checks every one against its sha1, and writes
generator/vanilla/.complete last:

    generator/vanilla/          the generator's PR files
    generator/cache/            vcache: the script index, the chests, the files a seed may change
    baseline/files/             the vanilla copy of every file a seed may change
    baseline/vanilla_data/      vanilla master tables and text ("play without a seed")
    <Magicite>/WorldsCollide/   the mod's Magicite folder (wc_live.write_baseline)

Layout: this file is generator/tools/ (in the bundle) or pr/tools/ (in the project).
"""
import argparse
import difflib
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
RECIPE = "gamedata_recipe.json"
SHIPPED = "gamedata"                        # generator/gamedata/<target>/<path>: our own files
MARK = ".complete"
BUNDLES = os.path.join("StreamingAssets", "aa", "StandaloneWindows64")
MAGICITE_CFG = os.path.join("BepInEx", "config", "silvris.magicite.cfg")
FOLDER = "WorldsCollide"
LEFT_OUT = ["generator/vanilla", "generator/cache", "baseline/files", "baseline/vanilla_data"]   # = modkit.json


def _sha1(data):
    return hashlib.sha1(data).hexdigest()


# --- what the plugin needs made from the PR's files (wc_bundle.py, from pr/vanilla)

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


# --- recipes: a made file from a game file

def _json(data):
    return json.loads(data.decode("utf-8-sig"))


def _compact(doc):
    return json.dumps(doc, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def _canon(data):
    """A JSON file one value a line, so a line patch holds only what changed."""
    return json.dumps(_json(data), ensure_ascii=False, indent=1).split("\n")


def _lines(data):
    return data.decode("utf-8").replace("\r\n", "\n").split("\n")


def _diff(a, b):
    """[[i1, i2, new lines], ...]: replace a[i1:i2] (the unchanged runs between are a's own).
    The common head and tail are cut off first (WC's changes sit in a few places: much faster)."""
    head = 0
    while head < len(a) and head < len(b) and a[head] == b[head]:
        head += 1
    tail = 0
    while tail < len(a) - head and tail < len(b) - head and a[-1 - tail] == b[-1 - tail]:
        tail += 1
    am, bm = a[head:len(a) - tail], b[head:len(b) - tail]
    sm = difflib.SequenceMatcher(None, am, bm, autojunk=len(am) > 6000)
    return [[head + i1, head + i2, bm[j1:j2]] for tag, i1, i2, j1, j2 in sm.get_opcodes() if tag != "equal"]


def _patch(a, ops):
    out, pos = [], 0
    for i1, i2, new in ops:
        out += a[pos:i1]
        out += new
        pos = i2
    return out + a[pos:]


def make(entry, src, made=None):
    """A recipe entry's bytes. src: game file bytes by path; made: earlier entries' bytes."""
    if "copy" in entry:
        return src[entry["copy"]]
    if "same" in entry:
        return made[entry["same"]]
    if "ai" in entry:
        doc = _json(src[entry["ai"]])
        doc["Name"] = entry["name"]
        return _compact(doc)
    if "json" in entry:
        return _compact(json.loads("\n".join(_patch(_canon(src[entry["json"]]), entry["ops"]))))
    if "text" in entry:
        text = "\n".join(_patch(_lines(src[entry["text"]]), entry["ops"]))
        return (text.replace("\n", "\r\n") if entry.get("crlf") else text).encode("utf-8")
    raise ValueError(f"recipe entry {entry}")


def _targets(gen, plugin, magicite):
    """(name, folder, paths inside it that are made) for each kind of made file."""
    base = os.path.join(plugin, "baseline")
    return [("magicite", magicite, None), ("baseline", base, ("files/", "vanilla_data/")),
            ("cache", os.path.join(gen, "cache"), None)]


def _walk(root, only=None):
    for r, dirs, fs in os.walk(root):
        dirs.sort()
        for f in sorted(fs):
            rel = os.path.relpath(os.path.join(r, f), root).replace("\\", "/")
            if only is None or rel.startswith(only):
                yield rel


def make_recipe(gen, plugin, magicite):
    """From the build's made files and pr/vanilla: gen/gamedata_recipe.json, gen/gamedata/ (ours) and
    gen/gamedata.json (the game files setup reads). Every entry is checked by making it."""
    import prdata as P
    van = P.VANILLA
    t0 = time.time()

    def game(rel):
        with open(os.path.join(van, *rel.split("/")), "rb") as f:
            return f.read()

    by_sha = {}
    for rel in _walk(van):
        if "/keys/" not in rel and not rel.endswith(".api.txt"):
            by_sha.setdefault(_sha1(game(rel)), rel)
    ai_by_body = {}
    for rel in _walk(os.path.join(van, "monster_ai")):
        if rel.endswith(".json") and "/keys/" not in rel:
            doc = _json(game("monster_ai/" + rel))
            doc.pop("Name", None)
            ai_by_body.setdefault(_compact(doc), "monster_ai/" + rel)
    shipped = os.path.join(gen, SHIPPED)
    shutil.rmtree(shipped, ignore_errors=True)
    recipe, made, src, first = {}, {}, {}, {}
    kinds = {}
    for target, root, only in _targets(gen, plugin, magicite):
        for rel in _walk(root, only):
            key = f"{target}|{rel}"
            with open(os.path.join(root, *rel.split("/")), "rb") as f:
                out = f.read()
            sha = _sha1(out)
            if target == "cache" or "/keys/" in rel or "/" not in rel:
                entry = {"ship": 1}     # ours: key lists, wc_baseline.json, the cache
                dst = os.path.join(shipped, target, *rel.split("/"))
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                with open(dst, "wb") as f:
                    f.write(out)
            elif sha in by_sha:
                entry = {"copy": by_sha[sha]}
            elif sha in first:
                entry = {"same": first[sha]}
            else:
                entry = None
                if rel.endswith(".json"):
                    doc = _json(out)
                    if isinstance(doc, dict) and "Name" in doc:
                        name = doc.pop("Name")
                        hit = ai_by_body.get(_compact(doc))
                        if hit:
                            entry = {"ai": hit, "name": name}
                # the game file at the same path (in the baseline, under files/ or vanilla_data/)
                grel = rel.split("/", 1)[1] if target == "baseline" else rel
                if entry is None and os.path.exists(os.path.join(van, *grel.split("/"))):
                    if rel.endswith(".json"):
                        entry = {"json": grel, "ops": _diff(_canon(game(grel)), _canon(out))}
                    else:
                        entry = {"text": grel, "crlf": int(b"\r\n" in out), "ops": _diff(_lines(game(grel)), _lines(out))}
                if entry is None:
                    raise SystemExit(f"recipe: {key} isn't from a game file and isn't one of ours: no way to make it")
                first[sha] = key
            entry["sha"] = sha
            if "ship" not in entry:
                for k in ("copy", "ai", "json", "text"):
                    if k in entry:
                        src[entry[k]] = game(entry[k])
                if make(entry, src, made) != out:
                    raise SystemExit(f"recipe: {key} ({next(k for k in entry if k != 'sha')}) doesn't make the build's file")
            made[key] = out
            recipe[key] = entry
            kind = next(k for k in entry if k != "sha")
            kinds[kind] = kinds.get(kind, 0) + 1
    # the game files setup reads: the generator's folder and every source above
    files = {}
    gen_van = os.path.join(gen, "vanilla")
    for rel in _walk(gen_van):
        if rel != MARK:
            with open(os.path.join(gen_van, *rel.split("/")), "rb") as f:
                files[rel] = [_sha1(f.read()), 1]
    for rel, data in src.items():
        files.setdefault(rel, [_sha1(data), 0])
    doc = {"_doc": "WorldsCollide: the game's files setup reads from the player's game (tools/wc_gamedata.py); "
                   "[sha1, 1 = generator/vanilla too]",
           "id": _sha1(json.dumps([sorted(files.items()), sorted((k, v["sha"]) for k, v in recipe.items())]).encode())[:16],
           "files": dict(sorted(files.items()))}
    with open(os.path.join(gen, MANIFEST), "w", encoding="utf-8", newline="\n") as f:   # a file a line
        f.write('{"_doc": %s,\n"id": "%s",\n"files": {\n' % (json.dumps(doc["_doc"]), doc["id"]))
        f.write(",\n".join(f"{json.dumps(k)}: {json.dumps(v)}" for k, v in doc["files"].items()))
        f.write("\n}}\n")
    with open(os.path.join(gen, RECIPE), "w", encoding="utf-8", newline="\n") as f:
        f.write("{\n" + ",\n".join(f"{json.dumps(k, ensure_ascii=False)}: {json.dumps(v, ensure_ascii=False, separators=(',', ':'))}"
                                    for k, v in recipe.items()) + "\n}\n")
    size = os.path.getsize(os.path.join(gen, RECIPE))
    print(f"recipe: {len(recipe)} made files ({', '.join(f'{n} {k}' for k, n in sorted(kinds.items()))}) from "
          f"{len(files)} game files; recipe {size // 1024} KB; {time.time() - t0:.0f} s")
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


def extract(doc, bundles):
    """The manifest's files from the game's bundles -> ({path: bytes}, differing, missing)."""
    by_group = {}
    for rel, (sha, _) in doc["files"].items():
        group, rest = rel.split("/", 1)
        by_group.setdefault(group, []).append((rest, sha))
    sys.path.insert(0, HERE)
    import pr_bundle
    out, differ, missing = {}, [], []
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
            out[f"{group}/{rest}"] = data
    return out, differ, missing


def _write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


def setup(game=None, magicite=None, plugin=PLUGIN, gen=GEN):
    """Makes the game data; True when every game file matched the manifest's (a changed game: False)."""
    t0 = time.time()
    print("setup: reading Worlds Collide's data out of this game (once)", flush=True)
    with open(os.path.join(gen, MANIFEST), encoding="utf-8") as f:
        doc = json.load(f)
    with open(os.path.join(gen, RECIPE), encoding="utf-8") as f:
        recipe = json.load(f)
    root, data = find_game(game or plugin)
    if not root:
        raise SystemExit(f"setup: no game folder (with *_Data/{BUNDLES.replace(os.sep, '/')}) at or above {game or plugin}")
    magicite = magicite or magicite_dir(root, data)
    src, differ, missing = extract(doc, os.path.join(data, BUNDLES))
    if missing:
        raise SystemExit(f"setup: {len(missing)} of the game's files aren't in its bundles "
                         f"(a different game version?): {', '.join(missing[:5])}")
    t1 = time.time()
    van = os.path.join(gen, "vanilla")
    shutil.rmtree(van, ignore_errors=True)
    for rel, (_, gen_too) in doc["files"].items():
        if gen_too:
            _write(os.path.join(van, *rel.split("/")), src[rel])
    # the Magicite folder is made beside its place and moved in last: Magicite never sees half of one
    work = os.path.join(gen, "setup_work")
    shutil.rmtree(work, ignore_errors=True)
    roots = {"magicite": os.path.join(work, "magicite"), "baseline": os.path.join(plugin, "baseline"),
             "cache": os.path.join(gen, "cache")}
    for d in ("files", "vanilla_data"):
        shutil.rmtree(os.path.join(roots["baseline"], d), ignore_errors=True)
    shutil.rmtree(roots["cache"], ignore_errors=True)
    sys.path.insert(0, HERE)
    import prdata
    prdata.VANILLA = van
    import vcache
    vcache._stamp = None
    stamp = vcache.stamp()      # the cache was made from the same files: this folder's stamp on it
    made, bad = {}, []
    for key, entry in recipe.items():
        target, rel = key.split("|", 1)
        if "ship" in entry:
            with open(os.path.join(gen, SHIPPED, target, *rel.split("/")), "rb") as f:
                out = f.read()
            if target == "cache":
                cached = json.loads(out)
                cached["stamp"] = stamp
                out = json.dumps(cached).encode("utf-8")
            elif _sha1(out) != entry["sha"]:
                bad.append(key)
        else:
            out = make(entry, src, made)
            if _sha1(out) != entry["sha"]:
                bad.append(key)
        made[key] = out
        _write(os.path.join(roots[target], *rel.split("/")), out)
    if bad:
        shutil.rmtree(work, ignore_errors=True)
        raise SystemExit(f"setup: {len(bad)} files came out different from this mod's (a different game version?): "
                         f"{', '.join(bad[:5])}")
    shutil.rmtree(magicite, ignore_errors=True)
    os.makedirs(os.path.dirname(magicite), exist_ok=True)
    shutil.move(roots["magicite"], magicite)
    shutil.rmtree(work, ignore_errors=True)
    with open(os.path.join(van, MARK), "w", encoding="utf-8") as f:
        f.write(doc["id"])
    note = f"; {len(differ)} differ from the game this mod was made with: {', '.join(differ[:3])}" if differ else ""
    print(f"setup: {len(src)} files from the game's bundles ({t1 - t0:.1f} s), {len(recipe)} made "
          f"({time.time() - t1:.1f} s) -> {magicite}{note}")
    return not differ


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
    t = time.time()
    p = subprocess.run([py, os.path.join(plugin, "generator", "tools", "wc_gamedata.py"), "setup", "--game", game,
                        "--magicite", magicite], cwd=os.path.join(plugin, "generator", "tools"), env=env,
                       capture_output=True, text=True, encoding="utf-8")
    print((p.stdout.strip().splitlines() or [""])[-1] if p.returncode == 0 else p.stdout[-500:] + p.stderr[-1500:])
    print(f"setup took {time.time() - t:.1f} s with the bundled Python")
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
    ap.add_argument("command", choices=["setup", "status", "recipe", "selftest"])
    ap.add_argument("dir", nargs="?", help="recipe / selftest: the build (pr/build/WorldsCollide)")
    ap.add_argument("--game", help="the game's folder (default: the one this plugin is in)")
    ap.add_argument("--magicite", help="the mod's Magicite folder (default: Magicite's import directory/WorldsCollide)")
    a = ap.parse_args()
    if a.command == "status":
        ok = status()
        print("set up" if ok else "not set up")
        return 0 if ok else 1
    build = os.path.abspath(a.dir or os.path.join(GEN, "build", "WorldsCollide"))
    if a.command == "recipe":
        sys.path.insert(0, HERE)
        plugin = os.path.join(build, "plugin")
        make_recipe(os.path.join(plugin, "generator"), plugin, os.path.join(build, "magicite", FOLDER))
        return 0
    if a.command == "selftest":
        sys.path.insert(0, HERE)
        from prkit import games
        work = os.path.join(os.path.dirname(build), "WorldsCollide-setup-test")
        return 0 if selftest(build, a.game or games.get("ff6").install, work) else 1
    setup(a.game, a.magicite)
    return 0


if __name__ == "__main__":
    sys.exit(main())
