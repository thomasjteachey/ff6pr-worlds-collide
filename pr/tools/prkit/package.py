"""A player zip for any mod in the manifest: extract it into the game folder (next to the .exe).

    BepInEx/plugins/<Mod>/...                                   the plugin (DLL + its data)
    <Game>_Data/StreamingAssets/Magicite/<Mod>/...              the mod's game data (Magicite)
    <Mod>-README.txt

Built from what deploy installs. Never a ROM (.sfc/.smc are refused, as are .pyc), and never the
game's own files: a file byte-identical to one in the game's extract (games.get(id).vanilla) is
refused (Magicite key lists aside: they're asset names). What a mod makes from the player's own
game instead (modkit.json package_leave_out / package_magicite) stays out, and such a mod packages
only once its plugin runs the script that makes it (player_setup: the DLL names it).
Needs BepInEx 6 (IL2CPP) and Magicite in the game first (the README says so).
"""
import collections
import hashlib
import os
import zipfile

from . import PR, mods

README = """{title}
{line}

Needs, installed in the game folder first (once):
  - BepInEx 6 for Unity IL2CPP games, x64 (a bleeding-edge build: builds.bepinex.dev;
    made with 6.0.0-be.697)
{magicite_line}Then extract this zip into the game folder (the one with {exe}), keeping its
folders. Remove the mod again by deleting {removal}.

Steam Deck / Linux (Proton): BepInEx only starts with this in the game's Steam launch options:
  WINEDLLOVERRIDES="winhttp=n,b" %command%
{setup}{note}"""

SETUP = """
The first start with the mod takes a minute or two longer (the console window stays up): it
makes the data it needs from your own copy of the game, since no game files come with this zip.
Later starts don't.
"""

SKIP_EXT = (".sfc", ".smc", ".pyc")
MIN_CHECKED = 64     # smaller files can match a game file by chance ("{}", short lists)


def readme(m):
    exe = m.game.title + ".exe"
    parts = []
    if m.has_plugin:
        parts.append(f"BepInEx/plugins/{m.name}")
    if m.magicite:
        parts.append(f"{m.game.title}_Data/StreamingAssets/Magicite/{m.name}")
    removal = " and\n".join(parts) if parts else "its files"
    note = ("\n" + m.readme_note + "\n") if m.readme_note else ""
    return README.format(title=m.title, line="=" * len(m.title), exe=exe, removal=removal,
                         magicite_line="  - Magicite, the Pixel Remaster mod loader\n" if m.magicite else "",
                         setup=SETUP if m.player_setup else "", note=note)


def _tree(src, dest, skip=(), leave_out=()):
    """(source path, zip name) for every file under src, but the left-out paths (relative to src)."""
    out = []
    for root, dirs, files in os.walk(src):
        rel_root = os.path.relpath(root, src).replace("\\", "/")
        rel_root = "" if rel_root == "." else rel_root + "/"
        dirs[:] = sorted(d for d in dirs if (rel_root + d) not in leave_out)
        for f in sorted(files):
            if f in skip or f.endswith(SKIP_EXT) or (rel_root + f) in leave_out:
                continue
            out.append((os.path.join(root, f), f"{dest}/{rel_root}{f}"))
    return out


def game_files_in(entries, vanilla):
    """The entries byte-identical to one of the game's own files (vanilla: its extract)."""
    by_size = collections.defaultdict(list)
    for root, _, files in os.walk(vanilla):
        for f in files:
            p = os.path.join(root, f)
            by_size[os.path.getsize(p)].append(p)
    digest = {}

    def sha(p):
        if p not in digest:
            with open(p, "rb") as fh:
                digest[p] = hashlib.sha1(fh.read()).digest()
        return digest[p]

    hits = []
    for src, name in entries:
        size = os.path.getsize(src)
        if size < MIN_CHECKED or "/keys/" in name or size not in by_size:
            continue
        if any(sha(p) == sha(src) for p in by_size[size]):
            hits.append(name)
    return hits


def runs_setup(m):
    """The plugin's DLL names its player_setup script (it runs it on the player's machine)."""
    needle = m.player_setup.encode("utf-16-le")     # .NET string literals are UTF-16
    for src, _ in m.plugin_files:
        if src.lower().endswith(".dll"):
            with open(m.path(src), "rb") as fh:
                if needle in fh.read():
                    return True
    return False


def zip_name(m):
    v = m.version()
    return f"{m.name}-PR{('-' + v) if v else ''}.zip"


def package(mod_id, out=None, root=PR):
    """Writes the zip; returns (path, number of files). Raises if a part isn't built, or if the
    zip would carry the game's own files."""
    m = mods.get(mod_id, root)
    missing = m.missing()
    if missing:
        raise FileNotFoundError("not built yet (kit.py deploy / the mod's build builds them): " + ", ".join(missing))
    if m.player_setup and not runs_setup(m):
        raise ValueError(f"{m.name}: its plugin doesn't run {m.player_setup} yet, so a player's install would have "
                         f"no game data (the zip leaves it out: {', '.join(m.package_leave_out)}). MODDING.md, "
                         f"'Install and ship'.")
    plugin = f"BepInEx/plugins/{m.name}"
    magicite = f"{m.game.title}_Data/StreamingAssets/Magicite/{m.name}"
    entries = []
    for src, name in m.plugin_files:
        if src.endswith(SKIP_EXT):
            raise ValueError(f"{src}: never packaged")
        entries.append((m.path(src), f"{plugin}/{name}"))
    for src, name in m.plugin_dirs:
        leave = {p[len(name) + 1:] for p in m.package_leave_out if p.startswith(name + "/")}
        if name in m.package_leave_out:
            continue
        entries += _tree(m.path(src), f"{plugin}/{name}", leave_out=leave)
    if m.magicite and m.package_magicite:
        entries += _tree(m.path(m.magicite), magicite, m.magicite_skip)
    vanilla = m.game.vanilla
    if os.path.isdir(vanilla):
        hits = game_files_in(entries, vanilla)
        if hits:
            groups = collections.Counter("/".join(h.split("/")[:5]) for h in hits)
            raise ValueError(f"{m.name}: {len(hits)} files are the game's own (not distributable), e.g. "
                             + "; ".join(f"{g} ({n})" for g, n in groups.most_common(4)))
    else:
        print(f"package: no extract of {m.game.title} at {vanilla}: the game-file check was skipped")
    out = out or os.path.join(root, "build", "dist")
    os.makedirs(out, exist_ok=True)
    path = os.path.join(out, zip_name(m))
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for src, name in entries:
            z.write(src, name)
        z.writestr(f"{m.name}-README.txt", readme(m))
    return path, len(entries)
