"""Put BepInEx + Magicite into another Pixel Remaster, copied from a game that has them (FF6).

    kit.py setup ff5 [--from ff6] [--dry-run]

BepInEx 6 (IL2CPP) and Magicite are the same files for every PR: the loader (winhttp.dll,
doorstop_config.ini, .doorstop_version), BepInEx/core, the .NET runtime (dotnet/) and Magicite's
plugin. What is per game, BepInEx makes itself at the game's first start with it: BepInEx/interop
(the game's code as .NET assemblies; plugins are built against it), unity-libs, cache, config.
So after this, start the game once yourself (it takes a minute at the first start); then
`kit.py games` shows interop: yes and plugins for that game can be built.

The files copied are listed in pr/games/<id>/install-files.txt: deleting them (and the generated
BepInEx folders) uninstalls. The Deck gets BepInEx with the first `kit.py deploy` of a mod.
"""
import os
import shutil

from . import games

LOADER = [".doorstop_version", "winhttp.dll", "doorstop_config.ini"]
DIRS = ["BepInEx/core", "BepInEx/patchers", "dotnet"]
MAGICITE = ["BepInEx/plugins/Magicite.dll", "BepInEx/plugins/Magicite.bundle", "BepInEx/plugins/Syldra.dll"]


def framework_files(source):
    """Every framework file in the source install, as forward-slash paths relative to it."""
    root = source.install
    out = []
    for f in LOADER + MAGICITE:
        if os.path.isfile(os.path.join(root, f)):
            out.append(f)
    for d in DIRS:
        base = os.path.join(root, *d.split("/"))
        for dirpath, _, files in os.walk(base):
            for f in files:
                out.append(os.path.relpath(os.path.join(dirpath, f), root).replace("\\", "/"))
    return sorted(out)


def setup(target_id, source_id="ff6", dry_run=False, force=False, out=print):
    target, source = games.get(target_id), games.get(source_id)
    if target.id == source.id:
        raise ValueError("source and target are the same game")
    if not source.installed or not source.status().get("bepinex"):
        raise FileNotFoundError(f"{source.title} PR has no BepInEx to copy from")
    if not target.installed:
        raise FileNotFoundError(f"{target.title} PR isn't installed (Steam's library list; PRKIT_{target.id.upper()}_DIR)")
    if target.running():
        raise RuntimeError(f"{target.title} is running: close it first")
    if target.status().get("bepinex") and not force:
        out(f"[setup] {target.title} PR already has BepInEx ({target.bepinex}); --force copies the framework again")
        return 0
    files = framework_files(source)
    if "BepInEx/core/BepInEx.Unity.IL2CPP.dll" not in files:
        raise FileNotFoundError(f"{source.install} has no BepInEx/core/BepInEx.Unity.IL2CPP.dll")
    out(f"[setup] {'would copy' if dry_run else 'copying'} {len(files)} framework files from {source.install} to {target.install}")
    if dry_run:
        for f in files[:12]:
            out(f"  {f}")
        out(f"  ... ({len(files)} in all)")
        return 0
    for f in files:
        dst = os.path.join(target.install, *f.split("/"))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(os.path.join(source.install, *f.split("/")), dst)
    os.makedirs(target.workspace, exist_ok=True)
    listing = os.path.join(target.workspace, "install-files.txt")
    with open(listing, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(f"# Files tools/kit.py setup {target.id} copied into {target.install} from {source.install}.\n")
        fh.write("# Delete these (plus BepInEx/ generated folders: interop, unity-libs, config, cache, LogOutput.log) to uninstall.\n")
        fh.write("".join(f"./{f}\n" for f in files))
    out(f"[setup] done; listed in {listing}")
    out(f"[setup] next: start {target.title} once yourself so BepInEx generates BepInEx/interop, close it, then "
        f"`kit.py games --write-props` and `kit.py extract {target.id}`")
    return 0
