"""Which story scenes can still play in a Worlds Collide game? (static audit, dev tool)

The user's playtest, 2026-11-12: walking down to the Blackjack's engine room played Setzer and
Cid's talk about Darill (sc_e_1033: the stairs' touch event; it plays once Gestahl's dinner is
over, ScenarioFlag2 1, which WC's start sets, until the scene's own flag, ScenarioFlag1 73).
WC plays no story scenes but its checks', so every scene a WC game can reach is a candidate.

For every PR map (map.csv) this runs, with WC's scripts (wc_events.build) over vanilla's:
  * the map's own script (sc_map_*, run on arrival): the entity layers it places (SetEntities)
    and anything it plays itself;
  * every scripted entity of entity_default and of the placed layers: touch events (object type
    2), talk (6), examine (5), vehicle landings (21), ...
following each script's control flow under a WC game's story flags: WC_GAME and START_FLAGS set,
the world's flags (WoB / WoR as WC's wheels leave them), every other story flag clear (--fresh)
or, by default, "may be either" for any flag some reachable script sets (a fixpoint: what a WC
game can get to). A script that reaches a story line (Msg/Select, not WC's own), a battle, or a
scene started by ChangeMap/ChangeScript is listed with the lines it can show.

--checks (the user's Deck, 2026-11-12: Gerad's engine-room scene, a check's own scene, which the
map sweep can't judge: its flags are WC's): every script with a WC:reward or WC:gate is run in
the offline tests' script machine (tests/test_wc.py Run) as a WC game takes it (a new game, the
gate met, the check not done), and so is the script its fight or map change leads to; listed:
the puppet routines it still calls (Call) and its story lines. WC trims its checks to the fight
and the reward, so a long list here is a scene to cut.

  .venv/Scripts/python tools/wc_scene_audit.py [--fresh] [--world wob|wor|both] [--kinds touch,entry,...]
                                               [--min-lines N] [--json OUT]
  .venv/Scripts/python tools/wc_scene_audit.py --checks [--min-calls N]
"""
import argparse
import csv
import json
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import prdata as P  # noqa: E402
import pr_script as S  # noqa: E402
import wc_events as W  # noqa: E402

MAPS = os.path.join("Assets", "GameAssets", "Serial", "Res", "Map")
FLAG_CATS = ("ScenarioFlag1", "ScenarioFlag2", "ScenarioFlag3", "ScenarioFlag4")
KIND = {2: "touch", 5: "examine", 6: "talk", 7: "chest", 10: "examine", 17: "shop", 21: "vehicle", 30: "switch"}
OPS = {"＝": lambda a, b: a == b, "≠": lambda a, b: a != b, "≧": lambda a, b: a >= b,
       "≦": lambda a, b: a <= b, "＞": lambda a, b: a > b, "＜": lambda a, b: a < b}
SCENE_OPS = ("Encount", "EncountBoss", "ChangePartySlot", "OrganizePartySlot")
JOIN = "メンバー加入："   # SysCall "member joins: <name>" (a vanilla recruit)


def master(name):
    return list(csv.DictReader(open(os.path.join(P.MASTER, name + ".csv"), encoding="utf-8")))


class Scripts:
    """Scripts by map folder, WC's copies (built to a temp dir) over vanilla's."""

    def __init__(self, wc_root):
        self.wc_root = wc_root
        self.names = {int(r["id"]): r["script_name"] for r in master("script")}
        self.cache = {}

    def folder_file(self, folder, name):
        for f in os.listdir(folder):
            if f.lower() == name.lower() + ".json":
                return os.path.join(folder, f)
        return None

    def load(self, name, folder=None):
        f = (folder and self.folder_file(folder, name)) or None
        if f is None:
            try:
                f = S.path(name)
            except KeyError:
                return None, None
        if f not in self.cache:
            rel = os.path.relpath(f, P.VANILLA)
            wc = os.path.join(self.wc_root, rel)
            self.cache[f] = (json.load(open(wc if os.path.exists(wc) else f, encoding="utf-8")), os.path.exists(wc))
        return self.cache[f][0], f


class State:
    def __init__(self, world, maybe=()):
        self.known = {(W.WC_FLAGS, W.WC_GAME): 1}
        for f in W.START_FLAGS:
            self.known[f] = 1
        wor = 1 if world == "wor" else 0
        self.known[W.COLLAPSED] = wor
        self.known[W.GOT_FALCON] = wor
        self.maybe = set(maybe) - {(W.WC_FLAGS, W.WC_GAME), W.COLLAPSED, W.GOT_FALCON}

    def value(self, cat, idx):
        if (cat, idx) in self.known and (cat, idx) not in self.maybe:
            return self.known[(cat, idx)]
        if cat == W.WC_FLAGS or (cat, idx) in self.maybe or cat not in FLAG_CATS:
            return None   # WC's own flags (checks, gates...), other variables: either way
        return 0


def labels(ms):
    return {n for n, m in enumerate(ms) if m["mnemonic"] == "Nop" and m.get("label")}


def run(sc, state):
    """Every instruction a script can reach from its start under `state`: {"msgs": [(i, key)],
    "scenes": [(op, name, map id)], "battles": [...], "layers": [...], "sets": {(cat, idx)}}."""
    ms = sc["Mnemonics"]
    targets = labels(ms)
    out = {"msgs": [], "scenes": [], "battles": [], "layers": [], "sets": set(), "puppets": 0, "joins": []}
    seen, todo = set(), [0]
    while todo:
        pc = todo.pop()
        while 0 <= pc < len(ms) and pc not in seen:
            seen.add(pc)
            m = ms[pc]
            o = m.get("operands") or {}
            iv, sv = o.get("iValues") or [0] * 8, o.get("sValues") or [""] * 8
            op = m["mnemonic"]
            nxt = [pc + 1]
            if op in ("Exit", "Return"):
                nxt = []
            elif op == "Jump":
                nxt = [iv[0] + 1]
            elif op == "Call":
                todo.append(iv[0] + 1)
            elif op == "Branch":
                v = state.value(sv[0], iv[0]) if sv[2] == "imm" else None
                if v is None or sv[1] not in OPS:
                    nxt = [pc + 1, iv[2] + 1]
                else:
                    nxt = [iv[2] + 1] if OPS[sv[1]](v, iv[1]) else [pc + 1]
            elif op.startswith("BranchBy") or op == "JumpIfSpecificLanguage":
                nxt = [pc + 1] + [t + 1 for t in iv if t > pc and t in targets]
            elif op in ("Msg", "Select") and sv[0] and not sv[0].startswith("WC_"):
                out["msgs"].append((pc, sv[0]))
            elif op == "ChangeMap" and sv[0]:
                out["scenes"].append(("ChangeMap", sv[0], iv[0]))
            elif op == "ChangeScript" and sv[0]:
                out["scenes"].append(("ChangeScript", sv[0], None))
                nxt = []
            elif op in SCENE_OPS:
                out["battles" if op.startswith("Encount") else "scenes"].append((op, tuple(iv[:3]), None))
            elif op == "SetEntities":
                out["layers"] += [x for x in sv if x]
            elif op in ("SetFlag", "ResetFlag") and sv[0] in FLAG_CATS:
                out["sets"].add((sv[0], iv[0]))
            elif op == "SetPuppet":
                out["puppets"] += 1
            elif op == "SysCall" and sv[0].startswith(JOIN):
                out["joins"].append(sv[0][len(JOIN):])
            todo += nxt[1:]
            pc = nxt[0] if nxt else -1
    out["msgs"].sort()
    return out


def entities(path):
    """[(layer file, name, object type, action, script id, x, y)] of one map entity file."""
    out = []

    def walk(o):
        if isinstance(o, dict):
            for ob in o.get("objects") or []:
                p = {x["name"]: x["value"] for x in ob.get("properties", [])}
                if p.get("script_id"):
                    out.append((os.path.basename(path), ob.get("name", ""), p.get("object_type"), p.get("action_id"),
                                p["script_id"], ob.get("x"), ob.get("y")))
            for v in o.values():
                walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)
    walk(json.load(open(path, encoding="utf-8")))
    return out


def map_folders():
    """[(map id, map name, folder)] for every map with a folder in vanilla."""
    out = []
    for r in master("map"):
        name, group = r["map_name"], r["asset_name"]
        top = "Map_" + name.split("_")[1]
        folder = os.path.join(P.VANILLA, group, MAPS, top, name)
        if os.path.isdir(folder):
            out.append((int(r["id"]), name, folder))
    return out


def follow(scripts, state, name, folder, map_of, depth=0, seen=None):
    """A script and the scenes it starts (ChangeMap's script runs in the new map): merged reach."""
    seen = set() if seen is None else seen
    if name.lower() in seen or depth > 6:
        return None
    seen.add(name.lower())
    sc, f = scripts.load(name, folder)
    if sc is None:
        return None
    r = run(sc, state)
    r["chain"] = [name]
    for op, target, map_id in list(r["scenes"]):
        if op not in ("ChangeMap", "ChangeScript"):
            continue
        sub_folder = map_of.get(map_id, folder) if op == "ChangeMap" else os.path.dirname(f)
        sub = follow(scripts, state, target, sub_folder, map_of, depth + 1, seen)
        if sub:
            r["msgs"] += [(n, k) for n, k in sub["msgs"]]
            r["battles"] += sub["battles"]
            r["sets"] |= sub["sets"]
            r["puppets"] += sub["puppets"]
            r["joins"] += sub["joins"]
            r["chain"] += sub["chain"]
    return r


def audit(scripts, state):
    """[{map, name, kind, entity, script, chain, msgs, battles, puppets}] and every flag set."""
    folders = map_folders()
    map_of = {mid: folder for mid, _, folder in folders}
    found, sets = [], set()
    for mid, mname, folder in folders:
        main = next((f for f in os.listdir(folder) if f.lower() == f"sc_{mname.lower()}.json"), None)
        placed = {"entity_default.json"}
        if main:
            r = follow(scripts, state, main[:-5], folder, map_of)
            if r:
                placed |= {x + ".json" for x in r["layers"]}
                sets |= r["sets"]
                if r["msgs"] or r["battles"] or r["joins"]:
                    found.append(dict(map=mid, name=mname, kind="entry", entity="", script=main[:-5], **_brief(r)))
        for layer in sorted(placed):
            p = os.path.join(folder, layer)
            if not os.path.exists(p):
                continue
            for lay, ename, otype, action, sid, x, y in entities(p):
                name = scripts.names.get(sid)
                if not name:
                    continue
                r = follow(scripts, state, name, folder, map_of)
                if not r:
                    continue
                sets |= r["sets"]
                if r["msgs"] or r["battles"] or r["joins"]:
                    found.append(dict(map=mid, name=mname, kind=KIND.get(otype, f"type{otype}"), layer=lay,
                                      entity=ename, at=(x // 16 if x is not None else None, y // 16 if y is not None else None),
                                      script=name, **_brief(r)))
    return found, sets


def _brief(r):
    return {"chain": r["chain"], "msgs": [k for _, k in r["msgs"]], "battles": [b[1] for b in r["battles"]],
            "puppets": r["puppets"], "joins": r["joins"], "sets": sorted(f"{c}:{i}" for c, i in r["sets"])}


def build_wc(root):
    os.makedirs(root, exist_ok=True)
    W.build(root)
    return root


def check_paths(root, min_calls):
    """--checks: [(script, world, calls, story lines, end, follow-up)] for the WC path of every
    check script (and of the script its fight or map change runs next)."""
    import glob
    sys.path.insert(0, os.path.join(os.path.dirname(HERE), "tests"))
    import test_wc as T
    built = {}
    for p in glob.glob(os.path.join(root, "**", "*.json"), recursive=True):
        built.setdefault(os.path.basename(p)[:-5].lower(), p)

    def load(name):
        p = built.get(name.lower())
        if p is None:
            try:
                p = S.path(name)
            except KeyError:
                return None
        return json.load(open(p, encoding="utf-8"))

    def game(world):
        st = T.State()
        T.Run(load("sc_e_0001"), st, syscalls={"WC:start": T.wc_start})
        st.set(*W.COLLAPSED, world)
        st.set(*W.GOT_FALCON, world)
        return st

    gates = {f"WC:gate {m}": (lambda s: s.set(W.WC_FLAGS, W.SCRATCH, 1)) for m in W.CHECK_FLAGS}
    out = []
    for name in sorted(built):
        sc = load(name)
        if not any(m["mnemonic"] == "SysCall" and m["operands"]["sValues"][0].startswith(("WC:reward", "WC:gate"))
                   for m in sc.get("Mnemonics", [])):
            continue
        for world in (0, 1):
            st = game(world)
            todo, seen = [(name, sc)], set()
            while todo:
                n, s = todo.pop()
                if n in seen or s is None:
                    continue
                seen.add(n)
                try:
                    r = T.Run(s, st, syscalls=gates, black=True, answers=[0] * 9)
                except AssertionError as e:
                    out.append((n, world, -1, [str(e)[:60]], None, None))
                    continue
                calls = [t[2][0] for t in r.did("Call")]
                lines = [t[3][0] for t in r.did("Msg") if t[3][0] and not t[3][0].startswith("WC_")]
                nxt = [t[3][0] for t in r.trace if t[1] in ("Encount", "EncountBoss") and t[3][0]]
                if r.end and r.end[0] == "ChangeMap" and r.end[3]:
                    nxt.append(r.end[3])
                if len(calls) >= min_calls or lines:
                    out.append((n, world, len(calls), lines, r.end, nxt))
                todo += [(x, load(x)) for x in nxt]
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--checks", action="store_true", help="the WC path through every check's own scripts")
    ap.add_argument("--min-calls", type=int, default=5, help="--checks: list scripts with this many puppet routines")
    ap.add_argument("--fresh", action="store_true", help="every story flag WC's start doesn't set is clear")
    ap.add_argument("--world", default="both", choices=("wob", "wor", "both"))
    ap.add_argument("--kinds", default="", help="comma list: entry,touch,talk,examine,vehicle,...")
    ap.add_argument("--min-lines", type=int, default=1)
    ap.add_argument("--min-puppets", type=int, default=4, help="also list silent scenes with this many puppet set-ups")
    ap.add_argument("--joins", action="store_true", help="only triggers that can reach a vanilla join")
    ap.add_argument("--json")
    a = ap.parse_args()
    if a.checks:
        with tempfile.TemporaryDirectory() as tmp:
            rows = check_paths(build_wc(tmp), a.min_calls)
        for n, world, calls, lines, end, nxt in rows:
            print(f"{n:22} {'wor' if world else 'wob'} routines {calls:3} lines {len(lines):2} "
                  f"end {end} next {nxt} | {S.text(lines[0])[:60].replace(chr(10), ' ') if lines else ''}")
        print(f"{len(rows)} check paths")
        return
    kinds = set(filter(None, a.kinds.split(",")))
    with tempfile.TemporaryDirectory() as tmp:
        scripts = Scripts(build_wc(tmp))
        rows = {}
        for world in (("wob", "wor") if a.world == "both" else (a.world,)):
            maybe = set()
            while True:
                found, sets = audit(scripts, State(world, maybe))
                if a.fresh or sets <= maybe:
                    break
                maybe |= sets
            for f in found:
                key = (f["map"], f["kind"], f.get("layer"), f["entity"], f["script"])
                row = rows.setdefault(key, dict(f, worlds=[]))
                if world not in row["worlds"]:
                    row["worlds"].append(world)
    out = [r for r in rows.values() if (not kinds or r["kind"] in kinds)
           and (len(r["msgs"]) >= a.min_lines or r["battles"] or r["joins"] or r["puppets"] >= a.min_puppets)
           and (not a.joins or r["joins"])]
    out.sort(key=lambda r: (r["map"], r["kind"], r["script"]))
    if a.json:
        json.dump(out, open(a.json, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    for r in out:
        first = S.text(r["msgs"][0])[:90].replace("\\n", " ") if r["msgs"] else ""
        print(f"{r['map']:4} {r['name']:14} {r['kind']:8} {'/'.join(r['worlds']):7} {r['script']:22} "
              f"lines {len(r['msgs']):3} puppets {r['puppets']:3} battles {len(r['battles'])} "
              f"{'joins ' + ','.join(r['joins']) + ' ' if r['joins'] else ''}"
              f"{'>'.join(r['chain'][1:3])} | {r['entity'][:24]} | {first}")
    print(f"{len(out)} scripted triggers")


if __name__ == "__main__":
    main()
