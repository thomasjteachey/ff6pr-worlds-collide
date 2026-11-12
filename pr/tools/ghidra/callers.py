"""Which methods of the PR's GameAssembly.dll call the given methods directly.

    pr/.venv/Scripts/python pr/tools/ghidra/callers.py <rva> [<rva> ...]

An RVA is a method's address from tools/il2cpp_out/dump.cs ("// RVA: 0xA8C760 ..."). Ghidra
runs here without auto-analysis (Il2CppDecompile.java), so it has no cross-references; this
scans the DLL's code for E8 / E9 rel32 (call / jmp) whose target is the RVA and names each
caller from script.json's method addresses (the method at or before the call site). The
managed code is in the "il2cpp" section, not .text. Virtual calls (vtable slots) and calls
through method pointers don't show up: a non-virtual wrapper (e.g. a Provider's method) does.
Found this way (2026-11-06): EquipmentAbilityProvider.GetTwoHandedMagnification is called by
PlayerCharacterParameter.ConfirmedAttack and GetArmConfirmedAttack (the two-handed grip)."""
import bisect
import json
import os
import struct
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
DLL = os.environ.get("FF6_GAME_DLL", r"D:\SteamLibrary\steamapps\common\FINAL FANTASY VI PR\GameAssembly.dll")
SCRIPT = os.path.join(ROOT, "tools", "il2cpp_out", "script.json")


def sections(data):
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    n = struct.unpack_from("<H", data, pe + 6)[0]
    opt = struct.unpack_from("<H", data, pe + 20)[0]
    sec = pe + 24 + opt
    for i in range(n):
        name = data[sec + 40 * i: sec + 40 * i + 8].rstrip(b"\0").decode()
        vsize, va, rsize, raw = struct.unpack_from("<IIII", data, sec + 40 * i + 8)
        yield name, va, raw, rsize


def main(rvas):
    data = open(DLL, "rb").read()
    js = json.load(open(SCRIPT, encoding="utf-8"))
    methods = sorted((m["Address"], m["Name"]) for m in js["ScriptMethod"])
    addrs = [a for a, _ in methods]

    def owner(rva):
        i = bisect.bisect_right(addrs, rva) - 1
        return methods[i] if i >= 0 else (0, "?")

    targets = {int(a, 16): a for a in rvas}
    hits = {t: [] for t in targets}
    for name, va, raw, size in sections(data):
        if name not in (".text", "il2cpp"):
            continue
        code = data[raw:raw + size]
        for op in (0xE8, 0xE9):
            start = 0
            while True:
                k = code.find(bytes([op]), start)
                if k < 0 or k + 5 > len(code):
                    break
                start = k + 1
                site = va + k
                dest = site + 5 + struct.unpack_from("<i", code, k + 1)[0]
                if dest in targets:
                    hits[dest].append((site, op))
    for t, sites in hits.items():
        print(f"== {targets[t]} {owner(t)[1]}: {len(sites)} call sites")
        seen = set()
        for site, op in sites:
            a, name = owner(site)
            if (a, name) not in seen:
                seen.add((a, name))
                print(f"   {'call' if op == 0xE8 else 'jmp '} from {name} (method {a:#x}, site +{site - a:#x})")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    main(sys.argv[1:])
