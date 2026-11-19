"""Calls a plugin makes into the game's Unity code that the game's build stripped.

IL2CPP keeps only the engine code the game itself uses. BepInEx's interop assemblies still list
the rest, but a stripped method's body there is `throw new NotSupportedException("Method
unstripping failed")`: the plugin builds and loads, then throws at the call (the Pixel Remasters'
GUI.DrawTexture and GUI.DrawTextureWithTexCoords do this; GUI.Box with a GUIStyle background works).
A headless run with no graphics never paints, so its tests can't see this; this check reads the
built DLL instead. It needs ilspycmd (dotnet tool install -g ilspycmd).

Matching is by method name per type: a name with any stripped overload is reported (check the
overload by hand if the one called is a kept one).
"""
import os
import re
import shutil
import subprocess

from . import PR

CACHE = os.path.join(PR, "build", "stripped-cache")
CALL = re.compile(r"\[(UnityEngine\.[A-Za-z0-9]+)\](UnityEngine\.[A-Za-z0-9.]+)::([A-Za-z_][A-Za-z0-9_]*)")
MEMBER = re.compile(r"\b(?:public|internal|private|protected)[^(=]*?\b([A-Za-z_][A-Za-z0-9_]*)\s*\(")
PROPERTY = re.compile(r"\b(?:public|internal|private|protected)[^(]*?\b([A-Za-z_][A-Za-z0-9_]*)\s*$")


def available():
    return shutil.which("ilspycmd") is not None


def _ilspy(*args):
    r = subprocess.run(["ilspycmd", *args], capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.stdout


def _stripped_names(game_id, interop, assembly, type_name):
    """Names of the type's methods (get_/set_ for properties) whose interop body is the failure."""
    path = os.path.join(CACHE, game_id, f"{type_name}.cs")
    if not os.path.exists(path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        src = _ilspy("-t", type_name, os.path.join(interop, assembly + ".dll"))
        with open(path, "w", encoding="utf-8") as f:
            f.write(src)
    with open(path, encoding="utf-8") as f:
        lines = f.read().splitlines()
    names = set()
    for i, line in enumerate(lines):
        if "Method unstripping failed" not in line:
            continue
        for k in range(i, max(-1, i - 8), -1):
            m = MEMBER.search(lines[k])
            if m:
                names.add(m.group(1))
                break
            accessor = lines[k].strip()
            if accessor in ("get", "set"):
                for j in range(k - 1, max(-1, k - 6), -1):
                    p = PROPERTY.search(lines[j])
                    if p:
                        names.add(f"{accessor}_{p.group(1)}")
                        break
                break
    return names


def check(dll, game):
    """[(type, [stripped method names called])] for one built plugin DLL."""
    interop = os.path.join(game.bepinex, "interop")
    calls = {}
    for assembly, type_name, method in CALL.findall(_ilspy("-il", dll)):
        calls.setdefault((assembly, type_name), set()).add(method)
    found = []
    for (assembly, type_name), methods in sorted(calls.items()):
        if not os.path.exists(os.path.join(interop, assembly + ".dll")):
            continue
        hit = methods & _stripped_names(game.id, interop, assembly, type_name)
        if hit:
            found.append((type_name, sorted(hit)))
    return found
