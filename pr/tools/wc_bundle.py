"""Build what the Worlds Collide plugin needs besides its DLL: the seed generator (Python,
Worlds Collide, our porter, the PR data it reads, the ROM) and the baselines.

  .venv/Scripts/python tools/wc_bundle.py [OUT_DIR] [--force] [--check] [--install DATA_DIR MAGICITE_DIR]
  (OUT_DIR default pr/build/WorldsCollide)

OUT_DIR/plugin/    -> BepInEx/plugins/WorldsCollide/ (deploy.sh adds the DLL + wc_flags.json)
  generator/python/      CPython, copied from this PC's install (the venv's base; no download):
                         runs on Windows and under Proton on the Steam Deck
  generator/tools/       the porter: the pr/tools scripts the generator runs or imports (generator_tools)
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

The bundle is remade only when something it is made from changed (a build takes 200-340 s):
build() runs under an audit hook that notes every file it reads or copies and every folder it lists,
plus the source of every module it ran (Python's own files: by version), and
OUT_DIR/bundle_inputs.json keeps them (a file's size and time; a folder's time and listing) with
a listing of what the build made. A run whose inputs all match says "bundle: up to date" in a
second or two; a copied file saved again with the same bytes doesn't count. A changed file the
build only copies (most tool scripts: the generator ships ~64, BNW's port_bnw_* and kit modules
such as snes_rooms included, which other sessions edit all day; WC's own files) is copied again with
its bytecode instead of a whole build; a change to anything the build reads or runs (pr/vanilla,
the modules it or the vanilla cache imports, the presets, the ROM) builds it again. --force builds
anyway. The record is written only by a build that ended well and that nothing it reads changed
under (one saved mid-build means the next run builds again; a copied file keeps its copy's times,
so the next run copies it again).
Not noticed: a file the build looked for and didn't find appearing later, a new dot-file or
__pycache__ in a listed folder, a new script in the tools folder that no generator script imports.
Use --force after changing anything outside the inputs the build reads (e.g. an environment
variable other than FF6_*).

--install DATA_DIR MAGICITE_DIR (deploy.sh, PC): then mirror generator/ and baseline/ into
DATA_DIR (BepInEx/WorldsCollide) and magicite/WorldsCollide into MAGICITE_DIR, each skipped when it
already matches what the build made (tools/quick_mirror.py); a bundle folder changed since its
build is remade first.
"""
import ast
import hashlib
import fnmatch
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PR = os.path.dirname(HERE)
PROJECT = os.path.dirname(PR)
sys.path.insert(0, HERE)
import kitpath  # noqa: E402,F401  (the modding kit's tools, when it's this repository's kit/ submodule)
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
# CPython's own test extensions (_testcapi, _ctypes_test, ...): nothing imports them outside its test suite
DLL_SKIP = ["_tkinter*", "tcl*", "tk*", "*.ico", "*.cat", "_test*", "_ctypes_test*"]
# the plugin starts python.exe with no window (WcGenerator.cs, WcGameData.cs): pythonw.exe is unused
EXE_SKIP = {"pythonw.exe"}
# the scripts the plugin runs (WcGenerator.cs: wc_seed.py; WcGameData.cs: wc_gamedata.py) and the
# ones build() runs in the bundle; generator_tools() adds everything they import or start, so dev
# tools (the game harness, the publish and SNES test scripts) stay out of the player zip
ENTRY_TOOLS = ["wc_seed", "wc_gamedata", "pr_script", "port_chests", "wc_live"]
# Other mods' own modules (repo_publish's BNW / BC / T-Edition lists): WC never runs them, but lazy or guarded
# imports in shared tools name them (port_ai -> port_boss_events for BNW's profile only, item_identity's CLI ->
# profile_tedition, ted_maps -> ted_events...), so the import walk skips them: no other mod's code in WC's
# player zip, and WC's zip no longer changes when theirs do (kit lead's finding, 2026-11-20)
OTHER_MODS = {"bnw_plugin_data", "port_bnw", "profile_bnw"}
_OTHER_PATTERNS = None


def _other_mod(name):
    global _OTHER_PATTERNS
    if _OTHER_PATTERNS is None:
        import repo_publish as R
        _OTHER_PATTERNS = [p for p in R.BNW_TOOLS + R.BC_TOOLS + R.TED_TOOLS if p.endswith(".py") and p != "kitpath.py"]
    return name in OTHER_MODS or any(fnmatch.fnmatch(name + ".py", p) for p in _OTHER_PATTERNS)


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


# --- remaking the bundle only when its inputs changed ----------------------------------------------

MANIFEST = "bundle_inputs.json"   # in OUT_DIR, written last by a build that ended well
MANIFEST_FORMAT = 2
# what --install mirrors: the bundle's part -> (its folder under OUT_DIR, where it goes)
PARTS = {"generator": (("plugin", "generator"), "data"), "baseline": (("plugin", "baseline"), "data"),
         "magicite": (("magicite", "WorldsCollide"), "magicite")}


def _norm(p):
    return os.path.normcase(os.path.abspath(os.fsdecode(p)))


def _under(p, roots):
    return any(p == r or p.startswith(r.rstrip(os.sep) + os.sep) for r in roots)


def _stats(folder, names):
    """{name: [size, mtime_ns]} for those of names in folder (one os.scandir: no file is opened, which
    Windows Defender would scan; a missing file is left out)."""
    want, out = set(names), {}
    try:
        with os.scandir(folder) as it:
            for e in it:
                if os.path.normcase(e.name) in want and not e.is_dir(follow_symlinks=False):
                    s = e.stat(follow_symlinks=False)
                    out[os.path.normcase(e.name)] = [s.st_size, s.st_mtime_ns]
    except OSError:
        pass
    return out


def _same_bytes(a, b):
    try:
        if os.path.getsize(a) != os.path.getsize(b):
            return False
        with open(a, "rb") as fa, open(b, "rb") as fb:
            while True:
                x, y = fa.read(1 << 20), fb.read(1 << 20)
                if x != y:
                    return False
                if not x:
                    return True
    except OSError:
        return False


def _listing(d):
    """A folder's names as the build sees them (copytree leaves dot-folders and __pycache__ out)."""
    names = sorted(n for n in os.listdir(d) if n != "__pycache__" and not n.startswith("."))
    return hashlib.sha1("\n".join(names).encode("utf-8")).hexdigest()


def _key(out):
    """What isn't a file but changes the bundle: the Python copied into it, where it's built, FF6_* settings."""
    return {"python": sys.version, "base": _norm(sys.base_prefix), "out": _norm(out),
            "env": {k: v for k, v in sorted(os.environ.items()) if k.upper().startswith("FF6_")}}


class Trace:
    """Every file a build reads and every folder it lists (sys.addaudithook: open, CopyFile2, listdir,
    scandir), and what it writes. A hook can't be removed, so it's switched with .on."""

    READ_EVENTS = {"shutil.copyfile": 0, "_winapi.CopyFile2": 0, "os.listdir": None, "os.scandir": None}
    WRITE_EVENTS = {"_winapi.CopyFile2": 1, "shutil.copyfile": 1, "os.mkdir": 0, "os.remove": 0, "os.rmdir": 0,
                    "os.rename": 1, "os.replace": 1, "shutil.rmtree": 0, "shutil.move": 1}

    def __init__(self):
        self.on = False
        self.read, self.listed, self.wrote, self.copies = set(), set(), set(), {}
        sys.addaudithook(self._hook)

    def _hook(self, event, args):
        if not self.on:
            return
        try:   # an exception here would fail the build's own call
            if event == "open":
                path, mode, flags = args
                if not isinstance(path, (str, bytes)):
                    return
                if mode is not None:
                    write = any(c in mode for c in "wax+")
                else:
                    write = bool(flags & (os.O_WRONLY | os.O_RDWR | os.O_APPEND | os.O_CREAT | os.O_TRUNC))
                (self.wrote if write else self.read).add(path)
                return
            i = self.READ_EVENTS.get(event, -1)
            if i == 0:   # a copy: its bytes only go into the copy (unless the build also opens it)
                self.copies.setdefault(args[0], []).append(args[1])
            elif i is None:
                self.listed.add(args[0] if args and isinstance(args[0], (str, bytes)) else ".")
            j = self.WRITE_EVENTS.get(event)
            if j is not None and isinstance(args[j], (str, bytes)):
                self.wrote.add(args[j])
        except Exception:
            pass


_TRACE = None
CHILD_MODULES = set()   # tool modules the bundle's own Python ran during build() (the vanilla cache)
RAN = set()             # (file, function qualname) of every function that ran during build(), both Pythons
MON = 4                 # sys.monitoring tool id (3 and 4 are free for tools)
# the same, in the bundle's Python (vanilla_cache): sys.argv[1] gets the list
_RAN_CHILD = ("import sys, json; R = set(); M = sys.monitoring; M.use_tool_id(4, 'wc_bundle'); "
              "M.register_callback(4, M.events.PY_START, lambda c, o: R.add((c.co_filename, c.co_qualname)) or M.DISABLE); "
              "M.set_events(4, M.events.PY_START); ")
_RAN_CHILD_END = "; M.set_events(4, 0); open(sys.argv[1], 'w').write(json.dumps(sorted(R)))"


def _watch_ran(on):
    """Note each function's first run (PY_START, then DISABLE: no cost after that)."""
    M = sys.monitoring
    if on:
        M.use_tool_id(MON, "wc_bundle")
        M.register_callback(MON, M.events.PY_START, lambda code, off: RAN.add((code.co_filename, code.co_qualname)) or M.DISABLE)
        M.set_events(MON, M.events.PY_START)
    else:
        M.set_events(MON, 0)
        M.register_callback(MON, M.events.PY_START, None)
        M.free_tool_id(MON)


def build_recorded(out):
    """build(out) under the trace, then the record of its inputs and outputs (MANIFEST) unless an input
    changed while it ran."""
    global _TRACE
    if _TRACE is None:
        _TRACE = Trace()
    tr = _TRACE
    tr.read.clear(), tr.listed.clear(), tr.wrote.clear(), tr.copies.clear()
    CHILD_MODULES.clear()
    RAN.clear()
    t0 = time.time_ns()
    tr.on = True
    _watch_ran(True)
    try:
        build(out)
    finally:
        tr.on = False
        _watch_ran(False)
    t = time.time()
    skip = [_norm(out), _norm(sys.base_prefix), _norm(os.environ.get("TEMP", out))]
    tool_folders = {_norm(d) for d in tool_dirs().values()}
    wrote = {_norm(p) for p in tr.wrote}
    wrote_in = {os.path.dirname(p) for p in wrote}
    out_n = _norm(out)
    copies = {}   # source -> its copies in OUT_DIR (relative)
    for s_, ds in tr.copies.items():
        rels = sorted({os.path.relpath(_norm(d), out_n) for d in ds if _under(_norm(d), [out_n])})
        if rels:
            copies[_norm(s_)] = rels
    # the modules the build ran (imported before the trace too, and those the bundle's Python ran for the
    # vanilla cache): their sources, not the bytecode read
    where = tool_dirs()
    mods = [getattr(m, "__file__", None) for n, m in list(sys.modules.items()) if n != "quick_mirror"]   # (deploy's, not the build's)
    mods += [os.path.abspath(__file__)]
    reads = {_norm(p) for p in list(tr.read) + [m for m in mods if m and m.endswith(".py")]}
    cache_mods = {_norm(os.path.join(where[n], n + ".py")) for n in CHILD_MODULES if n in where} - reads
    keep = lambda p: not (_under(p, skip) or p in wrote or "__pycache__" in p.split(os.sep) or p.endswith(".pyc"))  # noqa: E731
    # files: {folder: {name: [size, mtime_ns, [its copies in OUT_DIR], "read" | "copy" | "cache"]}}, stats from
    # one listing per folder. "copy": only copied into the bundle, its stats the copy's (copy2 keeps them: the
    # version copied, even if it changed later in the build); a change copies it again (update_copies).
    # "cache": a copied module the bundle's Python ran for the vanilla cache (port_ai, snes_ai...): a change
    # copies it again and makes the cache again, and only a different cache builds the bundle again.
    by_dir = {}
    for p in reads:
        if keep(p):
            by_dir.setdefault(os.path.dirname(p), {})[os.path.basename(p)] = "read"
    for p in copies:
        if keep(p) and p not in reads:
            by_dir.setdefault(os.path.dirname(p), {})[os.path.basename(p)] = "cache" if p in cache_mods else "copy"
    files, dirs, during, n, n_copy = {}, {}, [], 0, 0
    for d, names in sorted(by_dir.items()):
        got = _stats(d, [k for k, kind in names.items() if kind == "read"])
        by_copy = set()
        for k, kind in names.items():
            # a copied file: the copy's stats (the version copied). A module the build ran that it also copies
            # (a shipped tool script) too: if it was saved during the build, the next run compares the
            # source with that copy (_unexecuted_change) and copies it again or builds again (what ran is
            # the copied version unless it was changed in a function that ran and changed back mid-build)
            if kind != "read" or (k.endswith(".py") and os.path.join(d, k) in copies):
                copy = os.path.join(out, copies[os.path.join(d, k)][0])
                try:
                    st = os.stat(copy)
                except OSError:
                    continue
                got[k] = [st.st_size, st.st_mtime_ns]
                by_copy.add(k)
                n_copy += kind != "read"
        if got:
            files[d] = {k: v + [copies.get(os.path.join(d, k), []), names[k]] for k, v in sorted(got.items())}
            n += len(got)
            during += [os.path.join(d, k) for k, v in got.items() if names[k] == "read" and k not in by_copy and v[1] >= t0]
    # the tools folder's listing only picks which tool scripts to copy: those are inputs themselves
    for d in sorted({_norm(p) for p in tr.listed}):
        if _under(d, skip) or d in tool_folders or os.path.basename(d) == "__pycache__" or not os.path.isdir(d):
            continue
        st = os.stat(d)
        dirs[d] = [st.st_mtime_ns, _listing(d)]
        if st.st_mtime_ns >= t0 and d not in wrote_in:
            during.append(d + os.sep)
    made = {name: _snapshot(os.path.join(out, *rel)) for name, (rel, _) in PARTS.items()}
    # an input it reads changed mid-build: the bundle may hold either version, so the record says so
    # ("stale": up_to_date builds again) and keeps the rest (what was made: --install's comparison)
    stale = [os.path.relpath(p, PROJECT) for p in during]
    _write_manifest(out, {"format": MANIFEST_FORMAT, "key": _key(out), "built": time.strftime("%Y-%m-%d %H:%M:%S"),
                          "stale": stale, "tools": _bundle_tools(out), "cache_modules": sorted(CHILD_MODULES & set(where)),
                          "ran": _ran_by_module(out, tool_folders),
                          "files": files, "dirs": dirs, "made": made})
    print(f"bundle: recorded {n} input files ({n_copy} only copied) in {len(files)} folders, "
          f"{len(dirs)} listed folders ({time.time() - t:.1f} s)")
    if stale:
        print(f"bundle: {len(stale)} inputs changed while it built ({', '.join(stale[:3])}): the next run builds again")


def _ran_by_module(out, tool_folders):
    """{tool module: [qualnames of its functions that ran]} (module-level code isn't listed: it always runs);
    "?" when the vanilla cache's list was lost (then no change counts as unexecuted)."""
    if ("?", "?") in RAN:
        return "?"
    bundle_tools = _norm(os.path.join(out, "plugin", "generator", "tools"))
    ran = {}
    for f, q in RAN:
        if not f or f.startswith("<") or not f.endswith(".py") or q == "<module>":
            continue
        d = _norm(os.path.dirname(f))
        if d in tool_folders or d == bundle_tools:
            ran.setdefault(os.path.normcase(os.path.basename(f))[:-3], set()).add(q)
    return {k: sorted(v) for k, v in sorted(ran.items())}


def _unexecuted_change(old, new, ran):
    """Does the new version of a module differ from the old only in functions that didn't run in the
    build (ran: their qualnames), so the build would have made the same bundle? Its other code (module
    level, class bodies, each function's decorators and arguments) must be the same, statement for
    statement; methods count as functions (Class.method); a new module-level function is fine if its
    name is new to the module and not a builtin. (Not seen: code that reads its own source.)"""
    import builtins
    try:
        with open(old, encoding="utf-8") as f:
            a = ast.parse(f.read())
        with open(new, encoding="utf-8") as f:
            b = ast.parse(f.read())
    except (OSError, SyntaxError, ValueError):
        return False
    fn = (ast.FunctionDef, ast.AsyncFunctionDef)
    known = set(dir(builtins))
    for n in ast.walk(a):
        if isinstance(n, ast.Name):
            known.add(n.id)
        elif isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            known.add(n.name)
        elif isinstance(n, (ast.Import, ast.ImportFrom)):
            known.update((x.asname or x.name).split(".")[0] for x in n.names)

    def sig(f):
        return [ast.dump(x) for x in f.decorator_list] + [ast.dump(f.args)] + [
            ast.dump(f.returns) if f.returns else ""] + [ast.dump(x) for x in getattr(f, "type_params", [])]

    def head(c):
        return [ast.dump(x) for x in c.decorator_list + c.bases + c.keywords + list(getattr(c, "type_params", []))]

    def ran_in(q):
        return any(r == q or r.startswith(q + ".") for r in ran)

    def same_body(old_body, new_body, prefix, top):
        """Statement for statement; functions may differ inside if they never ran; classes: the same test
        on their body (their methods' qualnames are Class.method); at module level a new function with a
        new name is fine."""
        olds = {n.name: n for n in old_body if isinstance(n, fn + (ast.ClassDef,))}

        def key(n):
            return (type(n).__name__, n.name) if isinstance(n, fn + (ast.ClassDef,)) else ast.dump(n)
        seq_a = [key(n) for n in old_body]
        seq_b = [key(n) for n in new_body if not (top and isinstance(n, fn) and n.name not in olds and n.name not in known)]
        if seq_a != seq_b:
            return False
        for n in new_body:
            o = olds.get(n.name) if isinstance(n, fn + (ast.ClassDef,)) else None
            if o is None or ast.dump(o) == ast.dump(n):
                continue
            q = prefix + n.name
            if isinstance(n, ast.ClassDef):
                if head(o) != head(n) or not same_body(o.body, n.body, q + ".", False):
                    return False
            elif sig(o) != sig(n) or ran_in(q):
                return False
        return True

    return same_body(a.body, b.body, "", True)


def _bundle_tools(out):
    tools = os.path.join(out, "plugin", "generator", "tools")
    return sorted(f[:-3] for f in os.listdir(tools) if f.endswith(".py"))


def _snapshot(path):
    import quick_mirror  # the modding kit's (kitpath)
    return quick_mirror.snapshot(path)


def _write_manifest(out, m):
    tmp = os.path.join(out, MANIFEST + ".tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(m, f, separators=(",", ":"))
    os.replace(tmp, os.path.join(out, MANIFEST))


def load_manifest(out):
    try:
        with open(os.path.join(out, MANIFEST), encoding="utf-8") as f:
            m = json.load(f)
        return m if m.get("format") == MANIFEST_FORMAT else None
    except (OSError, ValueError):
        return None


def up_to_date(out):
    """-> ("current", manifest, []) when the last recorded build's inputs all still match; ("copies", manifest,
    [changed files that are only copied into the bundle]) when only such files changed (update_copies);
    ("build", why, []) otherwise."""
    m = load_manifest(out)
    if m is None:
        return "build", "no record of a finished build", []
    if m["key"] != _key(out):
        return "build", "Python, OUT_DIR or FF6_* settings differ from the last build's", []
    if m.get("stale"):
        return "build", f"{', '.join(m['stale'][:3])} changed while the last build ran", []
    missing = [n for n, (rel, _) in PARTS.items() if not os.path.isdir(os.path.join(out, *rel))]
    if missing:
        return "build", f"{', '.join(missing)} missing from {out}", []
    changed, copied, refreshed = [], [], 0
    for d, entries in m["files"].items():
        got = _stats(d, entries)
        for name, (size, mtime, copies, kind) in entries.items():
            now = got.get(name)
            if now == [size, mtime]:
                continue
            p = os.path.join(d, name)
            # saved again with the same bytes: compared with its copy in the bundle where it has one
            if now and copies and now[0] == size and _same_bytes(p, os.path.join(out, copies[0])):
                entries[name][1] = now[1]
                refreshed += 1
                continue
            if now and kind != "copy" and copies and p.endswith(".py") and m.get("ran") != "?" and _unexecuted_change(
                    os.path.join(out, copies[0]), p, m.get("ran", {}).get(name[:-3], [])):
                kind = "copy"   # (no function the build ran changed: the same bundle, only this file's copy)
                UNRAN.add(p)
            (copied if now and kind in ("copy", "cache") else changed).append(p)
    for d, (mtime, listing) in m["dirs"].items():
        try:
            st = os.stat(d)
            if st.st_mtime_ns == mtime:
                continue
            if _listing(d) == listing:   # a file in it replaced (saved through a rename): same names
                m["dirs"][d][0] = st.st_mtime_ns
                refreshed += 1
                continue
        except OSError:
            pass
        changed.append(d + os.sep)
    if changed:
        return "build", f"{len(changed)} inputs changed: " + ", ".join(os.path.relpath(c, PROJECT) for c in changed[:4]) + (
            f" (+{len(changed) - 4})" if len(changed) > 4 else ""), []
    if refreshed:
        _write_manifest(out, m)
    return ("copies" if copied else "current"), m, copied


def update_copies(out, m, paths):
    """Copy files that are only copied into the bundle (tool scripts, WC's own files) again, with their
    bytecode, instead of a whole build; when one is a module the vanilla cache ran, make the cache again
    and keep it only if it came out the same. -> None when done, else why a build is needed instead
    (the bundle is then left to the build)."""
    import importlib.util
    roots = {name: os.path.normcase(os.path.join(*rel)) for name, (rel, _) in PARTS.items()}
    gen = os.path.join(out, "plugin", "generator")
    redo, touched = [], []
    # the scripts the generator ships (generator_tools: what its scripts import, lazily too) may differ
    # now: a changed script imports another one, or no longer does. Only changes the build didn't run got
    # here, so a script added is copied in, and one dropped goes, unless the build ran it.
    try:
        now_tools = sorted(generator_tools())
    except SyntaxError as e:
        return f"a tool script doesn't parse ({e.filename}:{e.lineno})"
    where = tool_dirs()
    tools_dir = os.path.join(gen, "tools")
    for name in sorted(set(m["tools"]) - set(now_tools)):
        rel = os.path.relpath(os.path.join(tools_dir, name + ".py"), out)
        for d, entries in m["files"].items():
            for fname, entry in list(entries.items()):
                if [os.path.normcase(x) for x in entry[2]] == [os.path.normcase(rel)]:
                    if entry[3] != "copy":
                        return f"{name}.py ran in the build and isn't shipped any more"
                    del entries[fname]
        for f in (os.path.join(tools_dir, name + ".py"), importlib.util.cache_from_source(os.path.join(tools_dir, name + ".py"))):
            if os.path.exists(f):
                os.remove(f)
            touched.append(f)
    for name in sorted(set(now_tools) - set(m["tools"])):
        src, dst = os.path.join(where[name], name + ".py"), os.path.join(tools_dir, name + ".py")
        shutil.copy2(src, dst)
        st = os.stat(dst)
        m["files"].setdefault(_norm(where[name]), {})[os.path.normcase(name + ".py")] = [
            st.st_size, st.st_mtime_ns, [os.path.relpath(dst, out)], "copy"]
        redo.append(dst)
        touched += [dst, importlib.util.cache_from_source(dst)]
    added = sorted(set(now_tools) - set(m["tools"]))
    dropped = sorted(set(m["tools"]) - set(now_tools))
    m["tools"] = now_tools
    paths = [p for p in paths if os.path.basename(p) in m["files"].get(os.path.dirname(p), {})]   # (not a dropped script)
    remake_cache = [p for p in paths if m["files"][os.path.dirname(p)][os.path.basename(p)][3] == "cache" and p not in UNRAN]
    for p in paths:
        d, name = os.path.split(p)
        entry = m["files"][d][name]
        for rel in entry[2]:
            dst = os.path.join(out, rel)
            shutil.copy2(p, dst)
            touched.append(dst)
            if dst.endswith(".py") and _under(_norm(dst), [_norm(gen)]):
                redo.append(dst)
                touched.append(importlib.util.cache_from_source(dst))
        st = os.stat(os.path.join(out, entry[2][0]))
        entry[0], entry[1] = st.st_size, st.st_mtime_ns
    if redo:   # as build() compiles them: by the bundle's own Python (copy2 kept the sources' times)
        subprocess.run([os.path.join(gen, "python", "python.exe"), "-m", "compileall", "-q"] + redo,
                       check=False, capture_output=True)
    if remake_cache:
        cache, prev = os.path.join(gen, "cache"), os.path.join(gen, "cache.prev")
        if not os.path.isdir(cache):
            return "the bundle has no vanilla cache"
        shutil.rmtree(prev, ignore_errors=True)
        os.rename(cache, prev)
        p, mods = vanilla_cache(gen)
        if p.returncode != 0 or not _same_trees(cache, prev):
            return (f"the vanilla cache came out different with {', '.join(os.path.basename(x) for x in remake_cache)}"
                    if p.returncode == 0 else f"the vanilla cache failed: {p.stderr.strip()[-300:]}")
        if sorted(mods & set(tool_dirs())) != m.get("cache_modules"):
            return "the vanilla cache runs a different set of modules now"
        shutil.rmtree(cache)
        os.rename(prev, cache)   # the same bytes: the build's own files (and times) back
    # what the build made, as --install compares it with the game's copy
    for f in touched:
        rel = os.path.relpath(os.path.normcase(f), os.path.normcase(out))
        for name, root in roots.items():
            if rel.startswith(root + os.sep):
                inner = rel[len(root) + 1:]
                parent = os.path.dirname(inner)
                if parent and parent not in m["made"][name]:
                    m["made"][name][parent] = None
                try:
                    st = os.stat(f)
                    m["made"][name][inner] = [st.st_size, st.st_mtime_ns]
                except OSError:   # (no bytecode made, a script dropped)
                    m["made"][name].pop(inner, None)
    _write_manifest(out, m)
    if added or dropped:
        print(f"bundle: tool scripts shipped now: {', '.join('+' + x for x in added)}{' ' if added and dropped else ''}"
              f"{', '.join('-' + x for x in dropped)}")
    return None


UNRAN = set()   # files up_to_date found changed only in functions the build didn't run


class BundleChanged(Exception):
    pass


def install(out, m, data_dir, magicite_dir):
    """Mirror the bundle's parts into the game (PARTS), each skipped when it already matches the build's
    listing (m: its manifest; None: the bundle as it is now). Raises BundleChanged when a part to copy
    no longer matches what the build made."""
    import quick_mirror
    to = {"data": data_dir, "magicite": magicite_dir}
    for name, (rel, where) in PARTS.items():
        t = time.time()
        src = os.path.join(out, *rel)
        dst = os.path.join(to[where], name) if where == "data" else to[where]
        made = m["made"][name] if m else quick_mirror.snapshot(src)
        have = quick_mirror.snapshot(dst)
        if not quick_mirror.differ(made, have):
            print(f"[deploy]   WC {name}: unchanged ({time.time() - t:.1f} s)")
            continue
        if m and quick_mirror.differ(made, quick_mirror.snapshot(src)):
            raise BundleChanged(f"{src} changed since its build")
        n, left = quick_mirror.mirror(src, dst, src_snap=made, dst_snap=have)
        print(f"[deploy]   WC {name}: mirrored ({n} differed{'; still differs: ' + ', '.join(left[:3]) if left else ''}) "
              f"({time.time() - t:.1f} s)")
        if left:
            raise SystemExit(f"bundle: {dst} still differs from the bundle after robocopy")


def python_runtime(dst):
    base = sys.base_prefix
    os.makedirs(dst, exist_ok=True)
    for f in os.listdir(base):
        if f.lower().endswith((".exe", ".dll")) and not f.lower().startswith(("tcl", "tk")) and f.lower() not in EXE_SKIP:
            shutil.copy2(os.path.join(base, f), os.path.join(dst, f))
    copytree(os.path.join(base, "DLLs"), os.path.join(dst, "DLLs"), skip_files=DLL_SKIP)
    copytree(os.path.join(base, "Lib"), os.path.join(dst, "Lib"), skip_dirs=LIB_SKIP)
    # unittest is imported by nothing we run, but keep the module importable for safety
    copytree(os.path.join(base, "Lib", "unittest"), os.path.join(dst, "Lib", "unittest"), skip_dirs={"test", "__pycache__"})
    # the PSF license asks that its notice ships with any copy of Python
    shutil.copy2(os.path.join(base, "LICENSE.txt"), os.path.join(dst, "LICENSE.txt"))
    return f"Python {sys.version.split()[0]} from {base}"


def tool_dirs():
    """{module: folder} for every tool script: this folder's and, when the modding kit is this
    repository's kit/ submodule, the kit's (kitpath.TOOLS); this folder's win."""
    out = {}
    for d in [x for x in (kitpath.TOOLS, HERE) if x]:
        out.update({f[:-3]: d for f in os.listdir(d) if f.endswith(".py")})
    return out


def generator_tools():
    """pr/tools modules the generator needs: ENTRY_TOOLS plus every module of this folder they
    import (anywhere in the file, lazy imports too) or name as a script ("wc_run.py": wc_seed starts it)."""
    local = tool_dirs()
    seen, todo = set(), list(ENTRY_TOOLS)
    while todo:
        mod = todo.pop()
        if mod in seen:
            continue
        seen.add(mod)
        # (not a read for build_recorded: which scripts ship is checked again before copies are updated)
        tracing = _TRACE is not None and _TRACE.on
        if tracing:
            _TRACE.on = False
        try:
            with open(os.path.join(local[mod], mod + ".py"), encoding="utf-8") as fh:
                tree = ast.parse(fh.read())
        finally:
            if tracing:
                _TRACE.on = True
        for n in ast.walk(tree):
            if isinstance(n, ast.Import):
                names = [a.name for a in n.names]
            elif isinstance(n, ast.ImportFrom) and n.level == 0 and n.module:
                names = [n.module]
            elif isinstance(n, ast.Constant) and isinstance(n.value, str) and re.fullmatch(r"\w+\.py", n.value):
                names = [n.value[:-3]]
            else:
                continue
            todo += [m.split(".")[0] for m in names if m.split(".")[0] in local and not _other_mod(m.split(".")[0])]
    return sorted(seen)


def build(out):
    t0 = time.time()
    shutil.rmtree(out, ignore_errors=True)
    plugin = os.path.join(out, "plugin")
    gen = os.path.join(plugin, "generator")
    notes = [python_runtime(os.path.join(gen, "python"))]
    tools = os.path.join(gen, "tools")
    os.makedirs(tools)
    needed = generator_tools()
    where = tool_dirs()
    for mod in needed:
        shutil.copy2(os.path.join(where[mod], mod + ".py"), tools)
    notes.append(f"{len(needed)} of {len(where)} tool scripts")
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
    p, mods = vanilla_cache(gen)
    CHILD_MODULES.update(mods)
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
    manifest = wc_gamedata.make_recipe(gen, plugin, magicite)   # what a player's setup makes, and from what
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


def vanilla_cache(gen):
    """generator/cache, made by the bundle's own Python from its own vanilla folder and tools (vcache.py).
    -> (the run, the modules it imported: -X importtime; build_recorded marks those tool scripts "cache").
    The functions it ran go into RAN."""
    import tempfile
    fd, ran = tempfile.mkstemp(suffix=".json")
    os.close(fd)
    p = subprocess.run([os.path.join(gen, "python", "python.exe"), "-X", "importtime", "-c", _RAN_CHILD +
                        "import pr_script, port_chests, wc_live; pr_script.index(); port_chests._scan_chests(); "
                        "print(len(wc_live.managed_files()))" + _RAN_CHILD_END, ran],
                       cwd=os.path.join(gen, "tools"), check=False, capture_output=True, text=True)
    mods = {line.rsplit("|", 1)[-1].strip().split(".")[0] for line in p.stderr.splitlines() if line.startswith("import time:")}
    try:
        with open(ran, encoding="utf-8") as f:
            RAN.update(tuple(x) for x in json.load(f))
    except (OSError, ValueError):
        RAN.add(("?", "?"))   # unknown: then no change counts as unexecuted (see "ran" in the record)
    os.remove(ran)
    return p, mods


def _same_trees(a, b):
    """Same files with the same bytes under both folders?"""
    import quick_mirror
    sa, sb = quick_mirror.snapshot(a), quick_mirror.snapshot(b)
    if sa is None or sb is None or set(sa) != set(sb):
        return False
    return all(v is None or _same_bytes(os.path.join(a, k), os.path.join(b, k)) for k, v in sa.items())


def check(out):
    gen = os.path.join(out, "plugin", "generator")
    py = os.path.join(gen, "python", "python.exe")
    dest = os.path.join(out, "check_seed")
    env = {k: v for k, v in os.environ.items() if not k.startswith(("PYTHON", "VIRTUAL_ENV"))}
    env["PYTHONIOENCODING"] = "utf-8"
    before = _snapshot(gen)
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
    # what the check's run added (its seed's mods/, bytecode caches) stays out of the bundle; the
    # build's own precompiled bytecode stays in (removing every __pycache__ took that out too)
    after = _snapshot(gen)
    for rel in sorted((k for k in after if k not in before), key=len):
        p = os.path.join(gen, rel)
        if os.path.isdir(p):
            shutil.rmtree(p, ignore_errors=True)
        elif os.path.exists(p):
            os.remove(p)
    import quick_mirror
    if quick_mirror.differ(before, _snapshot(gen)):   # it changed a file the build made: build it again next time
        try:
            os.remove(os.path.join(out, MANIFEST))
        except OSError:
            pass
    return ok


def main():
    import argparse
    sys.stdout.reconfigure(line_buffering=True)   # each line as it comes (deploy.sh and the test runner read a pipe)
    ap = argparse.ArgumentParser(description="Build the Worlds Collide generator bundle (only when its inputs changed).")
    ap.add_argument("out", nargs="?", default=os.path.join(PR, "build", "WorldsCollide"))
    ap.add_argument("--force", action="store_true", help="build even when nothing it reads changed")
    ap.add_argument("--check", action="store_true", help="make a seed with the bundled Python to prove it works")
    ap.add_argument("--install", nargs=2, metavar=("DATA_DIR", "MAGICITE_DIR"),
                    help="then mirror it into the game (BepInEx/WorldsCollide, Magicite/WorldsCollide)")
    ap.add_argument("--status", action="store_true", help="only say whether it would build, copy files again or do nothing")
    a = ap.parse_args()
    out = os.path.abspath(a.out)
    t = time.time()
    state, m, copied = ("build", "--force", []) if a.force else up_to_date(out)
    if a.status:
        print(f"bundle: {state}" + (f" ({m})" if state == "build" else "") +
              (f": {', '.join(os.path.relpath(p, PROJECT) for p in copied)}" if copied else "") + f" ({time.time() - t:.1f} s)")
        return
    if state == "copies":
        why = update_copies(out, m, copied)
        if why:
            state, m = "build", why
        else:
            names = [os.path.basename(p) for p in copied]
            print(f"bundle: up to date after copying {len(copied)} changed files again ({', '.join(names[:4])}"
                  f"{f' +{len(names) - 4}' if len(names) > 4 else ''}; built {m['built']}; {time.time() - t:.1f} s) -> {out}")
    if state == "current":
        print(f"bundle: up to date (built {m['built']}; {sum(len(e) for e in m['files'].values())} input files, "
              f"{len(m['dirs'])} folders checked in {time.time() - t:.1f} s) -> {out}")
    elif state == "build":
        print(f"bundle: building ({m})")
        build_recorded(out)
    if a.check and not check(out):
        sys.exit(1)
    if a.install:
        # (no record: the bundle as it is, compared the full way)
        try:
            install(out, load_manifest(out), *a.install)
        except BundleChanged as e:
            print(f"bundle: building again ({e})")
            build_recorded(out)
            install(out, load_manifest(out), *a.install)


if __name__ == "__main__":
    main()
