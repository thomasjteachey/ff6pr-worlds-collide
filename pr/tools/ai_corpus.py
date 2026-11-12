"""Vanilla SNES AI scripts paired with their PR AI JSON, for studying the translation."""
import json
import os

import ff6snes as S
import prdata as P
import snes_ai as A

AI_DIR = os.path.join(P.VANILLA, "monster_ai", "Assets", "GameAssets", "Serial", "Res", "Battle", "MonsterAI")
VANILLA_ROM = P.VANILLA_ROM


def load_pr(name):
    """A vanilla PR AI script by name, or None if it isn't among the extracted files."""
    path = os.path.join(AI_DIR, name + ".json")
    if not os.path.exists(path):
        return None
    return json.load(open(path, encoding="utf-8"))


def pr_lines(doc):
    out = []
    for n, m in enumerate(doc["Mnemonics"]):
        o = m["operands"]
        iv, rv = list(o["iValues"]), list(o["rValues"])
        sv = [x for x in o["sValues"] if x]
        while iv and iv[-1] == 0:
            iv.pop()
        while rv and rv[-1] == 0:
            rv.pop()
        s = f"{n:2d} " + (m["label"] + ": " if m["label"] else "") + m["mnemonic"]
        s += (f" i{iv}" if iv else "") + (f" r{[round(x, 2) for x in rv]}" if rv else "") + (f" s{sv}" if sv else "")
        out.append(s)
    return out


class Corpus:
    def __init__(self):
        self.rom = S.Rom(VANILLA_ROM)
        self.enemies = S.enemies(self.rom)
        self.monster = P.Table("monster")
        self.scripts = P.Table("monster_ai_script")

    def pr_name(self, eid):
        sid = int(self.monster[eid + 1]["script_id"])
        return self.scripts[sid]["script_name"] if sid in self.scripts.rows else None

    def pair(self, eid):
        name = self.pr_name(eid)
        return A.script(self.rom, eid), (load_pr(name) if name else None)

    def find(self, pred):
        for e in range(384):
            (m, c), doc = self.pair(e)
            if doc and pred(m + c):
                yield e, m, c, doc

    def show(self, eid):
        (m, c), doc = self.pair(eid)
        print(f"=== {eid} {self.enemies[eid].name}  [{self.pr_name(eid)}]")
        print("  SNES main   :", A.fmt(m))
        print("  SNES counter:", A.fmt(c))
        if doc:
            print("  PR:", " | ".join(pr_lines(doc)))
