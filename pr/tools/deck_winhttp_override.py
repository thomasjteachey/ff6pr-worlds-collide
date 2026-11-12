"""Run ON the Steam Deck (deploy.sh pipes it over ssh): make FF6 PR's Proton prefix load
the native winhttp.dll (BepInEx's loader), the same as the Steam launch option
WINEDLLOVERRIDES="winhttp=n,b" %command%. Idempotent; keeps a backup of user.reg the
first time. The game must not be running (Wine rewrites user.reg on exit).

Other Pixel Remasters (tools/kit.py deploy): python3 - <steam app id> "<process name>"
"""
import os
import shutil
import subprocess
import sys

APP = sys.argv[1] if len(sys.argv) > 1 else "1173820"  # FINAL FANTASY VI (Pixel Remaster)
PROCESS = sys.argv[2] if len(sys.argv) > 2 else "FINAL FANTASY VI"
REG = os.path.expanduser(f"~/.local/share/Steam/steamapps/compatdata/{APP}/pfx/user.reg")
BS, Q = chr(92), chr(34)  # backslash and quote, spelled out so no shell can mangle them
HEADER = "[Software" + BS * 2 + "Wine" + BS * 2 + "DllOverrides]"
ENTRY = Q + "winhttp" + Q + "=" + Q + "native,builtin" + Q

if subprocess.run(["pgrep", "-f", PROCESS + "[.]exe"], capture_output=True).returncode == 0:
    sys.exit("[deck] the game is running: not touching its Proton prefix")
if not os.path.exists(REG):
    sys.exit(f"[deck] no Proton prefix yet ({REG}): start the game once, then deploy again")

text = open(REG, encoding="utf-8").read()
lines = text.split("\n")
start = next((i for i, l in enumerate(lines) if l.startswith(HEADER)), None)
if start is None:  # Proton normally creates this section; add it if missing
    lines += ["", HEADER, ENTRY]
else:
    end = start + 1
    while end < len(lines) and lines[end].strip():
        end += 1
    if any(l.startswith(Q + "winhttp" + Q + "=") for l in lines[start:end]):
        print("[deck] winhttp override already set")
        sys.exit(0)
    lines.insert(end, ENTRY)
backup = REG + ".bak-before-bnw"
if not os.path.exists(backup):
    shutil.copy2(REG, backup)
open(REG, "w", encoding="utf-8").write("\n".join(lines))
print(f"[deck] set winhttp=native,builtin in {PROCESS}'s Proton prefix (app {APP})")
