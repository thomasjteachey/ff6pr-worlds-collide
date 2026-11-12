"""The Pixel Remasters, where they're installed, and the paths every tool needs.

    from prkit import games
    g = games.get("ff5")
    g.installed, g.install, g.exe, g.data_dir, g.bundles, g.magicite_dir, g.plugins_dir
    g.vanilla           # this project's extract of the game's data (kit.py extract ff5)
    g.deck_install      # the same game on the Steam Deck

Installs are found the way Steam records them: every library in libraryfolders.vdf, then
steamapps/appmanifest_<appid>.acf's "installdir". Overrides, for odd setups:
    PRKIT_<ID>_DIR        the game's folder, e.g. PRKIT_FF5_DIR=E:/Games/FINAL FANTASY V PR
    PRKIT_STEAM_LIBRARIES Steam library folders (os.pathsep-separated) instead of Steam's list
    PRKIT_DECK_HOST       ssh host of the Steam Deck (default "deck")
Only FF6's names are checked on this machine (2026-11-11); the others' app ids and folder names
are Steam's, and exe/data folders are found on disk rather than assumed.
"""
import glob
import os
import re
import subprocess
from dataclasses import dataclass, field

from . import PR

STEAM_DEFAULTS = [r"C:\Program Files (x86)\Steam", r"C:\Program Files\Steam"]
DECK_STEAM = "/home/deck/.local/share/Steam"
MAGICITE_CFG = os.path.join("BepInEx", "config", "silvris.magicite.cfg")


@dataclass(frozen=True)
class Spec:
    id: str             # ff1..ff6
    number: int         # 1..6
    title: str          # the store/process name
    appid: int          # Steam app id
    installdir: str     # Steam's default folder name under steamapps/common
    serial_ns: str = field(default="")   # the game's own code (Serial.FF6.*), beside the shared Last.* engine


SPECS = [
    Spec("ff1", 1, "FINAL FANTASY", 1173770, "FINAL FANTASY PR", "Serial.FF1"),
    Spec("ff2", 2, "FINAL FANTASY II", 1173780, "FINAL FANTASY II PR", "Serial.FF2"),
    Spec("ff3", 3, "FINAL FANTASY III", 1173790, "FINAL FANTASY III PR", "Serial.FF3"),
    Spec("ff4", 4, "FINAL FANTASY IV", 1173800, "FINAL FANTASY IV PR", "Serial.FF4"),
    Spec("ff5", 5, "FINAL FANTASY V", 1173810, "FINAL FANTASY V PR", "Serial.FF5"),
    Spec("ff6", 6, "FINAL FANTASY VI", 1173820, "FINAL FANTASY VI PR", "Serial.FF6"),
]
BY_ID = {s.id: s for s in SPECS}
ALIASES = {**{str(s.number): s.id for s in SPECS},
           **{s.title.lower(): s.id for s in SPECS},
           "ffv": "ff5", "ffvi": "ff6", "ffiv": "ff4", "ffiii": "ff3", "ffii": "ff2", "ffi": "ff1"}


def resolve_id(name):
    """'ff5', '5', 'FFV', 'final fantasy v' -> 'ff5'."""
    key = str(name).strip().lower()
    if key in BY_ID:
        return key
    if key in ALIASES:
        return ALIASES[key]
    raise KeyError(f"unknown game {name!r}; one of {', '.join(BY_ID)}")


# --- Steam ----------------------------------------------------------------------------------------

def _vdf_paths(text):
    return [p.replace("\\\\", "\\") for p in re.findall(r'"path"\s+"([^"]+)"', text)]


def _steam_root():
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as k:
            return os.path.normpath(winreg.QueryValueEx(k, "SteamPath")[0])
    except (ImportError, OSError):
        return None


def libraries():
    """Steam library folders on this PC (each has a steamapps/ folder)."""
    env = os.environ.get("PRKIT_STEAM_LIBRARIES")
    if env is not None:
        return [p for p in env.split(os.pathsep) if p]
    roots = [r for r in [_steam_root()] + STEAM_DEFAULTS if r]
    found = []
    for root in roots:
        vdf = os.path.join(root, "steamapps", "libraryfolders.vdf")
        if not os.path.exists(vdf):
            continue
        with open(vdf, encoding="utf-8", errors="replace") as f:
            for p in [root] + _vdf_paths(f.read()):
                p = os.path.normpath(p)
                if p.lower() not in (x.lower() for x in found) and os.path.isdir(p):
                    found.append(p)
        break
    return found


def _manifest_installdir(text):
    m = re.search(r'"installdir"\s+"([^"]+)"', text)
    return m.group(1) if m else None


def find_install(spec):
    """(folder, installdir) of a game on this PC, or (None, spec.installdir)."""
    env = os.environ.get(f"PRKIT_{spec.id.upper()}_DIR")
    if env:
        return (os.path.normpath(env) if os.path.isdir(env) else None), os.path.basename(os.path.normpath(env))
    for lib in libraries():
        acf = os.path.join(lib, "steamapps", f"appmanifest_{spec.appid}.acf")
        if not os.path.exists(acf):
            continue
        with open(acf, encoding="utf-8", errors="replace") as f:
            installdir = _manifest_installdir(f.read()) or spec.installdir
        path = os.path.join(lib, "steamapps", "common", installdir)
        if os.path.isdir(path):
            return os.path.normpath(path), installdir
    return None, spec.installdir


# --- one game -------------------------------------------------------------------------------------

class Game:
    """A PR game: where it is on this PC and the Deck, and where this project keeps its files."""

    def __init__(self, spec, install=None, installdir=None):
        self.spec = spec
        self.id, self.title, self.appid, self.number = spec.id, spec.title, spec.appid, spec.number
        if install is None and installdir is None:
            install, installdir = find_install(spec)
        self.install = install
        self.installdir = installdir or spec.installdir

    def __repr__(self):
        return f"Game({self.id}, {self.install or 'not installed'})"

    @property
    def installed(self):
        return bool(self.install) and os.path.isdir(self.install)

    def _need(self):
        if not self.installed:
            raise FileNotFoundError(f"{self.title} PR isn't installed on this PC "
                                    f"(set PRKIT_{self.id.upper()}_DIR if Steam's list misses it)")
        return self.install

    # the game's exe and Unity data folder: <stem>.exe beside <stem>_Data
    @property
    def exe(self):
        root = self._need()
        want = os.path.join(root, self.title + ".exe")
        if os.path.exists(want):
            return want
        for exe in sorted(glob.glob(os.path.join(root, "*.exe"))):
            stem = os.path.splitext(os.path.basename(exe))[0]
            if os.path.isdir(os.path.join(root, stem + "_Data")):
                return exe
        return want

    @property
    def process(self):
        """The process name (exe without .exe), as tasklist / pgrep / BepInEx see it."""
        if self.installed:
            return os.path.splitext(os.path.basename(self.exe))[0]
        return self.title

    @property
    def data_name(self):
        return self.process + "_Data"

    @property
    def data_dir(self):
        return os.path.join(self._need(), self.data_name)

    @property
    def streaming_assets(self):
        return os.path.join(self.data_dir, "StreamingAssets")

    @property
    def bundles(self):
        return os.path.join(self.streaming_assets, "aa", "StandaloneWindows64")

    @property
    def bepinex(self):
        return os.path.join(self._need(), "BepInEx")

    @property
    def plugins_dir(self):
        return os.path.join(self.bepinex, "plugins")

    @property
    def shelf_dir(self):
        """Where deploy sets aside a mod that can't be installed beside another (BepInEx loads nothing there)."""
        return os.path.join(self.bepinex, "mod-shelf")

    @property
    def magicite_setting(self):
        """Magicite's import directory as configured ([Magicite Paths] Import Directory)."""
        cfg = os.path.join(self._need(), MAGICITE_CFG)
        try:
            with open(cfg, encoding="utf-8", errors="replace") as f:
                for line in f:
                    t = line.strip()
                    if t.lower().startswith("import directory") and "=" in t:
                        return t.split("=", 1)[1].strip()
        except OSError:
            pass
        return "%StreamingAssets%/Magicite"

    @property
    def magicite_dir(self):
        v = self.magicite_setting.replace("%StreamingAssets%", self.streaming_assets).replace("%DataPath%", self.data_dir)
        return os.path.normpath(v)

    def status(self):
        """What's set up in this install: BepInEx, its generated interop, Magicite, mods."""
        if not self.installed:
            return {"installed": False}
        plugins = sorted(d for d in os.listdir(self.plugins_dir)
                         if os.path.isdir(os.path.join(self.plugins_dir, d))) if os.path.isdir(self.plugins_dir) else []
        magicite = sorted(os.listdir(self.magicite_dir)) if os.path.isdir(self.magicite_dir) else []
        return {
            "installed": True,
            "bepinex": os.path.exists(os.path.join(self.bepinex, "core", "BepInEx.Unity.IL2CPP.dll")),
            "interop": os.path.exists(os.path.join(self.bepinex, "interop", "Assembly-CSharp.dll")),
            "magicite": os.path.exists(os.path.join(self.plugins_dir, "Magicite.dll")),
            "plugins": plugins,
            "magicite_mods": magicite,
        }

    def running(self):
        """Is the game running on this PC? (tasklist; False where there is none)."""
        try:
            out = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {self.process}.exe", "/NH"],
                                 capture_output=True, text=True, timeout=20).stdout
        except (OSError, subprocess.SubprocessError):
            return False
        return f"{self.process}.exe".lower() in out.lower()

    # --- this project's files for the game ---------------------------------------------------------

    @property
    def workspace(self):
        """pr/games/<id>: the extract, API dump and notes for a game (FF6 predates it: pr/)."""
        return PR if self.id == "ff6" else os.path.join(PR, "games", self.id)

    @property
    def vanilla(self):
        """The game's own data, extracted (kit.py extract): <group>/<container path> + keys."""
        return os.path.join(self.workspace, "vanilla")

    @property
    def api_dump(self):
        """Every type/field/method of the game's code (kit.py api), for finding hook targets."""
        return os.path.join(self.vanilla, "Assembly-CSharp.api.txt")

    # --- the Steam Deck --------------------------------------------------------------------------------

    @property
    def deck_host(self):
        return os.environ.get("PRKIT_DECK_HOST", "deck")

    @property
    def deck_install(self):
        return f"{DECK_STEAM}/steamapps/common/{self.installdir}"

    @property
    def deck_prefix(self):
        return f"{DECK_STEAM}/steamapps/compatdata/{self.appid}/pfx"

    def rel(self, path):
        """A path inside the install as a forward-slash relative path (tar, the Deck)."""
        return os.path.relpath(path, self._need()).replace("\\", "/")


def props_text():
    """plugin/PrKit/Games.props: each installed game's folder, for PrKit.props' GameDir."""
    lines = ["<Project>",
             "  <!-- written by tools/kit.py games (its write-props option) from Steam's library list on this PC;",
             "       PrKit.props picks GameDir from it by the plugin's PrGame. Write it again after installing a game. -->",
             "  <PropertyGroup>"]
    for g in all_games():
        if g.installed:
            lines.append(f"    <PrGameDir_{g.id}>{g.install}</PrGameDir_{g.id}>")
        else:
            lines.append(f"    <!-- {g.id}: {g.title} PR isn't installed -->")
    lines += ["  </PropertyGroup>", "</Project>", ""]
    return "\n".join(lines)


def write_props(path=None):
    path = path or os.path.join(PR, "plugin", "PrKit", "Games.props")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(props_text())
    return path


_cache = {}


def get(name):
    gid = resolve_id(name)
    if gid not in _cache:
        _cache[gid] = Game(BY_ID[gid])
    return _cache[gid]


def all_games():
    return [get(s.id) for s in SPECS]


def forget():
    """Drop cached lookups (tests change PRKIT_* variables)."""
    _cache.clear()
