"""Offline test suite for the Brave New World Magicite mod (pr/mods/BraveNewWorld).

Checks the mod the way the game will read it, without running the game:
  * format: partial CSV headers match the game's exactly, rows are well formed,
    keys files point at real files, text lines are single-line TSV
  * references: every id the mod writes points at something that exists after
    merging the mod's partial tables over vanilla
  * AI: compiled scripts follow the structure and operand shapes the game's own
    430 scripts use (learned from pr/vanilla), and jump targets are valid
  * BNW fidelity: spot checks against BNW's manual

  pr/.venv/Scripts/python -m unittest discover -s pr/tests -v
"""
import csv
import glob
import io
import json
import os
import sys
import unittest
from collections import Counter, defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
PR = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(PR, "tools"))
import prdata as P  # noqa: E402

MOD_NAME = os.environ.get("PR_MOD", "BraveNewWorld")  # which ported mod to test
MOD = os.path.join(PR, "mods", MOD_NAME)
MOD_MASTER = os.path.join(MOD, "master", "Assets", "GameAssets", "Serial", "Data", "Master")
MOD_MESSAGE = os.path.join(MOD, "message", "Assets", "GameAssets", "Serial", "Data", "Message", "system_en.txt")
MOD_AI = os.path.join(MOD, "monster_ai", "Assets", "GameAssets", "Serial", "Res", "Battle", "MonsterAI")
VAN_AI = os.path.join(P.VANILLA, "monster_ai", "Assets", "GameAssets", "Serial", "Res", "Battle", "MonsterAI")


def read_csv(path):
    with open(path, encoding="utf-8", newline="") as f:
        rows = list(csv.reader(io.StringIO(f.read())))
    return rows[0], [r for r in rows[1:] if r]


_merged = {}


def merged(name):
    """Vanilla table with the mod's partial rows applied (as Magicite does)."""
    if name not in _merged:
        t = P.Table(name)
        path = os.path.join(MOD_MASTER, name + ".csv")
        if os.path.exists(path):
            header, rows = read_csv(path)
            for r in rows:
                t.rows[int(r[0])] = dict(zip(header, r))
        _merged[name] = t
    return _merged[name]


def ids(name):
    return set(merged(name).rows)


def groups(name):
    return {int(r["group_id"]) for r in merged(name)}


def mod_tables():
    return sorted(os.path.splitext(os.path.basename(p))[0] for p in glob.glob(os.path.join(MOD_MASTER, "*.csv")))


class Format(unittest.TestCase):
    def test_mod_exists(self):
        self.assertTrue(os.path.isdir(MOD_MASTER), "run tools/port_bnw.py first")

    def test_csv_headers_match_game(self):
        for name in mod_tables():
            with self.subTest(table=name):
                vh, _ = read_csv(os.path.join(P.MASTER, name + ".csv"))
                mh, _ = read_csv(os.path.join(MOD_MASTER, name + ".csv"))
                self.assertEqual(mh, vh)

    def test_csv_rows_well_formed(self):
        for name in mod_tables():
            header, rows = read_csv(os.path.join(MOD_MASTER, name + ".csv"))
            _, vrows = read_csv(os.path.join(P.MASTER, name + ".csv"))
            numeric = [all(v[i].lstrip("-").isdigit() for v in vrows if v[i] not in ("", "None")) for i in range(len(header))]
            seen = set()
            for r in rows:
                with self.subTest(table=name, row=r[0]):
                    self.assertEqual(len(r), len(header))
                    self.assertTrue(r[0].isdigit())
                    self.assertNotIn(r[0], seen, "duplicate id")
                    seen.add(r[0])
                    for i, v in enumerate(r):
                        if numeric[i] and v not in ("None",):
                            self.assertRegex(v, r"^-?\d+$", f"column {header[i]}")
                        self.assertNotIn(",", v)

    def test_keys_point_at_files(self):
        for keyfile in glob.glob(os.path.join(MOD, "*", "keys", "*.json")):
            group_dir = os.path.dirname(os.path.dirname(keyfile))
            doc = json.load(open(keyfile, encoding="utf-8"))
            self.assertEqual(len(doc["keys"]), len(doc["values"]), keyfile)
            for v in doc["values"]:
                with self.subTest(file=v):
                    self.assertTrue(glob.glob(os.path.join(group_dir, *v.split("/")) + ".*"), f"missing {v}")

    def test_message_lines(self):
        vanilla = P.messages()
        with open(MOD_MESSAGE, encoding="utf-8") as f:
            for n, line in enumerate(f, 1):
                line = line.rstrip("\r\n")
                with self.subTest(line=n):
                    key, sep, value = line.partition("\t")
                    self.assertEqual(sep, "\t")
                    self.assertNotIn("\t", value)
                    self.assertTrue(key in vanilla or key.startswith("MSG_BNW_"), f"key {key!r} not in the game's text")
                    self.assertTrue(value.strip(), "empty text")


class References(unittest.TestCase):
    def check(self, table, col, valid, allow_zero=True):
        """Flag ids the mod wrote that point nowhere (the game itself has a few dangling ones)."""
        vanilla = P.Table(table)
        bad = []
        for r in merged(table):
            v = int(r[col]) if r[col] not in ("", "None") else 0
            if v == 0 and allow_zero:
                continue
            vrow = vanilla.rows.get(int(r["id"]))
            if vrow is not None and vrow[col] == r[col]:
                continue  # unchanged from the game
            if v not in valid:
                bad.append((r["id"], v))
        self.assertEqual(bad[:10], [], f"{table}.{col}: {len(bad)} rows point at missing ids")

    def test_ability_effects(self):
        self.check("ability", "battle_function_group_id", ids("function_group"))
        self.check("ability", "attribute_group_id", groups("attribute_group"))

    def test_function_group_targets(self):
        conds, cgroups = ids("condition"), groups("condition_group")
        bad = []
        for r in merged("function_group"):
            for k in (1, 2, 3):
                f, v = int(r[f"function{k}_id"]), int(r[f"function{k}_value1"])
                if f == 9 and v not in conds:
                    bad.append((r["id"], f, v))
                if f in (10, 12) and v not in cgroups:
                    bad.append((r["id"], f, v))
        self.assertEqual(bad, [])

    def test_condition_group_members(self):
        self.check("condition_group", "condition_id", ids("condition"), allow_zero=False)

    def test_monsters(self):
        self.check("monster", "resistance_attribute", groups("attribute_group"))
        self.check("monster", "resistance_condition", groups("condition_group"))
        self.check("monster", "initial_condition", groups("condition_group"))
        self.check("monster", "script_id", ids("monster_ai_script"))
        self.check("monster", "monster_flag_group_id", groups("monster_flag_group"))
        content = ids("content")
        for k in range(1, 9):
            self.check("monster", f"drop_content_id{k}", content)
        for k in range(1, 5):
            self.check("monster", f"steal_content_id{k}", content)

    def test_equipment(self):
        jobs = ids("job_group")
        self.check("weapon", "equip_job_group_id", jobs)
        self.check("armor", "equip_job_group_id", jobs)
        self.check("armor", "resistance_attribute", groups("attribute_group"))
        self.check("armor", "resistance_condition", groups("condition_group"))
        self.check("armor", "wear_condition_group_id", groups("condition_group"))

    def test_learning_shops_characters(self):
        content = ids("content")
        self.check("learning", "content_id", content)
        self.check("product", "content_id", content)
        for k in range(1, 7):
            self.check("character_status", f"content_id{k}", content)
        espers = {int(r["id"]) for r in merged("content") if r["type_id"] == "9"}
        for r in merged("learning"):
            if r["type_id"] == "2" and r["value1"] != "0":
                self.assertIn(int(r["value1"]), espers, f"learning {r['id']}")
                if MOD_NAME == "BraveNewWorld":  # BNW teaches esper spells only through the SP bank
                    self.assertEqual("0", r["value2"], f"learning {r['id']} rate")

    def test_ai_script_files_exist(self):
        mod_files = {os.path.splitext(f)[0] for f in os.listdir(MOD_AI)} if os.path.isdir(MOD_AI) else set()
        van_files = {os.path.splitext(f)[0] for f in os.listdir(VAN_AI)}
        used = {int(r["script_id"]) for r in merged("monster")}
        scripts = merged("monster_ai_script")
        missing = [(sid, scripts[sid]["script_name"]) for sid in used if sid in scripts.rows
                   and scripts[sid]["script_name"] not in mod_files | van_files
                   and scripts[sid]["script_name"].startswith("sc_ai_bnw")]
        self.assertEqual(missing, [])


# --- AI scripts: learn the game's own conventions from vanilla -------------------------
class AiRules:
    def __init__(self):
        self.vocab = Counter()
        self.shape = defaultdict(Counter)       # mnemonic -> (padded?, len i, len r)
        self.jump_hits = defaultdict(Counter)   # (mnemonic, operand index) -> hits
        self.jump_seen = defaultdict(Counter)
        self.comparators = defaultdict(set)
        self.top_keys = None
        for path in glob.glob(os.path.join(VAN_AI, "*.json")):
            doc = json.load(open(path, encoding="utf-8"))
            if "Mnemonics" not in doc:
                continue
            self.top_keys = self.top_keys or set(doc)
            ms = doc["Mnemonics"]
            labeled = {i for i, m in enumerate(ms) if m["label"]}
            for m in ms:
                o = m.get("operands") or {}
                if not all(k in o for k in ("iValues", "rValues", "sValues")):
                    continue  # one odd vanilla file uses another layout
                name = m["mnemonic"]
                self.vocab[name] += 1
                self.shape[name][(len(o["iValues"]) == 8, len(o["iValues"]), len(o["rValues"]))] += 1
                for s in o["sValues"]:
                    if s:
                        self.comparators[name].add(s)
                for i, v in enumerate(o["iValues"]):
                    if v:
                        self.jump_seen[name][i] += 1
                        if v in labeled:
                            self.jump_hits[name][i] += 1

    def jump_operands(self, name):
        return {i for i, n in self.jump_seen[name].items() if n and self.jump_hits[name][i] / n >= 0.9}


_rules = None


def rules():
    global _rules
    if _rules is None:
        _rules = AiRules()
    return _rules


def compiled_scripts():
    return sorted(glob.glob(os.path.join(MOD_AI, "*.json")))


class AiScripts(unittest.TestCase):
    def test_compiled_scripts_listed_in_keys(self):
        keyfile = os.path.join(MOD, "monster_ai", "keys", "bnw_ai.json")
        listed = set(json.load(open(keyfile, encoding="utf-8"))["keys"]) if os.path.exists(keyfile) else set()
        self.assertEqual({os.path.splitext(os.path.basename(p))[0] for p in compiled_scripts()}, listed)

    def test_scripts(self):
        R = rules()
        abilities = ids("ability")

        def text_keys(fname):
            path = os.path.join(MOD, "message", "Assets", "GameAssets", "Serial", "Data", "Message", fname)
            if not os.path.exists(path):
                return set()
            return {line.split("	", 1)[0] for line in open(path, encoding="utf-8").read().splitlines() if "	" in line}
        mod_lines, mod_speakers = text_keys("story_mes_en.txt"), text_keys("story_cha_en.txt")
        for path in compiled_scripts():
            name = os.path.basename(path)
            doc = json.load(open(path, encoding="utf-8"))
            ms = doc["Mnemonics"]
            with self.subTest(script=name):
                self.assertEqual(set(doc), R.top_keys)
                self.assertEqual(doc["Segments"][0]["Count"], len(ms))
                labels = [m["label"] for m in ms if m["label"]]
                self.assertEqual(len(labels), len(set(labels)), "duplicate labels")
                self.assertEqual(ms[0]["label"], "Main")
                self.assertEqual(ms[-1]["mnemonic"], "Exit")
                labeled = {i for i, m in enumerate(ms) if m["label"]}
                depth = 0
                rows_in_table = 0
                for n, m in enumerate(ms):
                    mnem, o = m["mnemonic"], m["operands"]
                    self.assertIn(mnem, R.vocab, f"#{n} unknown mnemonic")
                    shape = (len(o["iValues"]) == 8, len(o["iValues"]), len(o["rValues"]))
                    if shape[0]:
                        self.assertIn(shape, R.shape[mnem], f"#{n} {mnem} operand shape {shape}")
                    else:
                        self.assertFalse(any(s[0] for s in R.shape[mnem]) and not any(not s[0] for s in R.shape[mnem]),
                                         f"#{n} {mnem} must be padded to 8 operands")
                    for s in o["sValues"]:
                        if s and s in mod_lines:  # the mod's own battle lines (battle_text)
                            self.assertIn(s, mod_speakers, f"#{n} {mnem} line {s} has no story_cha entry")
                        elif s:
                            self.assertIn(s, R.comparators[mnem], f"#{n} {mnem} comparator {s}")
                    for i in R.jump_operands(mnem):
                        if i < len(o["iValues"]) and o["iValues"][i]:
                            self.assertIn(o["iValues"][i], labeled, f"#{n} {mnem} jumps to unlabeled line")
                    if mnem == "StartTable":
                        depth += 1
                        rows_in_table = 0
                        self.assertEqual(depth, 1, f"#{n} nested table")
                    elif mnem == "EndTable":
                        depth -= 1
                        self.assertEqual(depth, 0, f"#{n} unbalanced EndTable")
                        self.assertGreater(rows_in_table, 0, f"#{n} empty table")
                        self.assertEqual(ms[n + 1]["mnemonic"], "JumpBattleAI", f"#{n} table not closed by JumpBattleAI")
                    # rows that give a table something to do (the game's own scripts have tables
                    # holding only a TimerAttackAct, a FinalAttackAct, or variable updates)
                    if mnem in ("Act", "LoopAct", "TimerAttackAct", "FinalAttackAct",
                                "CounterAct", "ValueCalc", "VariableAssign"):  # counter tables may only update variables
                        rows_in_table += 1
                    if mnem in ("Act", "CounterAct", "FinalAttackAct", "TimerAttackAct"):
                        self.assertEqual(len(o["iValues"]), len(o["rValues"]), f"#{n} ids/rates mismatch")
                        self.assertAlmostEqual(sum(o["rValues"]), 100, delta=0.5, msg=f"#{n} rates")
                    if mnem in ("Act", "LoopAct", "CounterAct", "FinalAttackAct", "TimerAttackAct"):
                        used = o["iValues"][1:] if mnem == "TimerAttackAct" else o["iValues"]  # first = timer
                        for a in used:
                            self.assertIn(a, abilities, f"#{n} ability {a} doesn't exist")
                self.assertEqual(depth, 0)


class Encounters(unittest.TestCase):
    def test_sets_reference_real_non_empty_parties(self):
        parties, monsters = merged("monster_party"), ids("monster")
        vanilla_sets = P.Table("monster_set")
        bad = []
        for r in merged("monster_set"):
            if vanilla_sets.rows.get(int(r["id"])) == r:
                continue  # untouched
            for k in range(1, 17):
                pid = int(r[f"monster_set{k}"])
                if not pid:
                    continue
                row = parties.rows.get(pid)
                members = [int(row[f"monster{j}"]) for j in range(1, 10) if row and row[f"monster{j}"] != "0"]
                if not row or not members or any(m not in monsters for m in members):
                    bad.append((r["id"], pid))
        self.assertEqual(bad, [])

    def test_veldt_offers_the_mods_formations(self):
        """The Veldt (tools/port_veldt.py, when the mod changes it): one entry per formation the
        mod's flags allow, fighting a party with that formation's monsters, opened by it."""
        path = os.path.join(MOD, "misc", "Assets", "GameAssets", "Serial", "Data", "Misc", "serial_misc_assets.json")
        if not os.path.exists(path):
            self.skipTest("the mod's Veldt is vanilla's")
        import ff6snes as SN
        import port_veldt as V
        import profiles
        rom = SN.Rom(profiles.PROFILES[MOD_NAME.lower().replace("bravenewworld", "bnw")].rom) if MOD_NAME == "BraveNewWorld" else None
        if rom is None:
            self.skipTest("no ROM for this mod")
        table = json.load(open(path, encoding="utf-8"))["beast_field_encount_table"]
        parties = merged("monster_party")
        entries = [e for g in table for e in g["party_table"]]
        self.assertEqual(len(entries), len(V.veldt_formations(rom)))
        self.assertEqual([e["flag_index"] for e in entries], list(range(len(entries))), "one unlock flag each, in order")
        for g, f in zip((g for g in table for _ in g["party_table"]), V.veldt_formations(rom)):
            self.assertEqual(g["group_id"], f // 8 + 1)
        bad = []
        for e, f in zip(entries, V.veldt_formations(rom)):
            row = parties.rows.get(e["monster_party_id"])
            members = sorted(int(row[f"monster{k}"]) - 1 for k in range(1, 10) if row and row[f"monster{k}"] != "0")
            if members != V.monsters(rom, f) or e["encount_target_party_id"] != e["monster_party_id"]:
                bad.append((f, e["monster_party_id"], members, V.monsters(rom, f)))
        self.assertEqual(bad, [], "Veldt entries (formation, party, its monsters, the formation's)")

    def test_leap_parties(self):
        """BNW's Leap needs a Veldt formation, wherever it's fought (the plugin's SpecialCommands
        offers Leap when the fight's party is on the loaded Veldt table): every PR party of each
        of the mod's Veldt formations is on the table, and none of another formation."""
        path = os.path.join(MOD, "misc", "Assets", "GameAssets", "Serial", "Data", "Misc", "serial_misc_assets.json")
        if not os.path.exists(path) or MOD_NAME != "BraveNewWorld":
            self.skipTest("the mod's Veldt is vanilla's")
        import ff6snes as SN
        import port_encounters as E
        import port_veldt as V
        import profiles
        rom = SN.Rom(profiles.PROFILES["bnw"].rom)
        on = set()
        for g in json.load(open(path, encoding="utf-8"))["beast_field_encount_table"]:
            for e in g["party_table"]:
                on |= {e["monster_party_id"], e["encount_target_party_id"], *e.get("encount_target_other_ids", [])}
        rows = merged("monster_party").rows
        veldt = set(V.veldt_formations(rom))
        of = {}
        for f in range(576):
            for p in E.party_ids(f, rows):
                of.setdefault(p, f)
        self.assertEqual(sorted((f, p) for f in veldt for p in E.party_ids(f, rows) if p not in on), [], "Veldt parties off the table")
        self.assertEqual(sorted(p for p in on if p in of and of[p] not in veldt), [], "parties of other formations on the table")

    def test_new_parties_use_distinct_cells(self):
        for r in merged("monster_party"):
            if int(r["id"]) >= 2000:
                used = [k for k in range(1, 10) if r[f"monster{k}"] != "0"]
                self.assertTrue(1 <= len(used) <= 9, r["id"])


class AiAbilityNames(unittest.TestCase):
    def test_every_ai_ability_has_a_name(self):
        named = {int(r["type_value"]) for r in merged("content") if r["type_id"] == "4"}
        missing = Counter()
        for path in compiled_scripts():
            for m in json.load(open(path, encoding="utf-8"))["Mnemonics"]:
                if m["mnemonic"] in ("Act", "LoopAct", "CounterAct", "FinalAttackAct"):
                    for a in m["operands"]["iValues"]:
                        if a not in named:
                            missing[a] += 1
        self.assertEqual(dict(missing), {}, "abilities used by AI without a content (name) row")


class MapFiles(unittest.TestCase):
    def test_chest_maps_match_vanilla_structure(self):
        for path in glob.glob(os.path.join(MOD, "map_*", "Assets", "**", "*.json"), recursive=True):
            if os.path.basename(path).startswith("sc_"):
                continue                     # scripts: a box's battle, a scripted chest's gift (below)
            rel = os.path.relpath(path, MOD)
            van = json.load(open(os.path.join(P.VANILLA, rel), encoding="utf-8"))
            mod = json.load(open(path, encoding="utf-8"))
            # NPCs the port added (port_merchants: script ids from 9901) aren't chest changes
            for layer in mod.get("layers", []):
                if layer.get("objects"):
                    layer["objects"] = [o for o in layer["objects"]
                                        if not any(p["name"] == "script_id" and p["value"] > 9900 for p in o.get("properties", []))]
            # a map group's script lists: only the scripts the port added (a merchant's, a new
            # box's), each in the mod beside its map's entities
            if os.path.basename(path) == "package.json":
                for m, vm in zip(mod.get("map", []), van.get("map", [])):
                    added = [x for x in m["script"] if x not in vm["script"]]
                    for x in added:
                        with self.subTest(file=rel, script=x["name"]):
                            self.assertTrue(os.path.exists(os.path.join(os.path.dirname(path), x["asset"] + ".json")))
                    m["script"] = [x for x in m["script"] if x not in added]
            with self.subTest(file=rel):
                self.assertEqual(diff_paths(van, mod), set(), "changed more than chest contents")

    def test_boxes_fight_the_mods_formations(self):
        """Every monster-in-a-box fights its formation as BNW has it (port_chests.seed_party):
        BNW renumbers formations, and a formation's number alone named vanilla's fight there (the
        Narshe box fought vanilla formation 256's four monsters, not BNW's one). Item chests BNW
        made boxes (South Figaro's Earring in both of the PR's copies, Mt. Zozo's Red Cap) are
        boxes in the PR too: a box script of their own, in their map's script list."""
        if MOD_NAME != "BraveNewWorld":
            self.skipTest("BNW's formations")
        import ff6snes as SN
        import port_chests as C
        import port_encounters as E
        import pr_script as S
        import profiles
        van, bnw = SN.Rom(P.VANILLA_ROM), SN.Rom(profiles.PROFILES["bnw"].rom)
        vc, bc = C.snes_chests(van), C.snes_chests(bnw)
        changed = {k for k, b in bc.items() if not vc.get(k) or (vc[k]["type"], vc[k]["contents"]) != (b["type"], b["contents"])}
        flags = C.match_flags(vc, changed=changed)
        moved = {old: new for new, old in C.moved_chests(vc, bc, flags).items()}
        parties, scripts = merged("monster_party"), merged("script")
        mod_scripts = {os.path.splitext(os.path.basename(f))[0]: f
                       for f in glob.glob(os.path.join(MOD, "map_*", "Assets", "**", "sc_*.json"), recursive=True)}
        checked, made = 0, 0
        for k, es in flags.items():
            b = bc.get(moved.get(k, k))
            if not b or b["type"] != C.MONSTER or not C.lineup(bnw, b["formation"]):
                continue
            want = C.lineup(bnw, b["formation"])
            for e in es:
                f = os.path.join(MOD, os.path.relpath(e["file"], P.VANILLA))
                doc = json.load(open(f if os.path.exists(f) else e["file"], encoding="utf-8"))
                obj = next(o for l in doc["layers"] for o in (l.get("objects") or []) if o["id"] == e["obj"])
                sid = next((p["value"] for p in obj["properties"] if p["name"] == "script_id"), 0)
                if not sid:
                    continue                 # a scripted chest (no battle in the PR: port_chests skips it)
                name = scripts[sid]["script_name"]
                sc = json.load(open(mod_scripts[name], encoding="utf-8")) if name in mod_scripts else S.load(name)
                battles = [m["operands"]["iValues"][0] for m in sc["Mnemonics"] if m["mnemonic"] in C.BATTLES]
                if not battles:
                    continue
                with self.subTest(chest=k, script=name):
                    for pid in battles:
                        self.assertEqual(E.party_members(parties[pid]), want, f"party {pid} for BNW formation {b['formation']}")
                    if sid > C.BOX_SCRIPT_IDS:
                        made += 1
                        pkg = json.load(open(os.path.join(os.path.dirname(os.path.dirname(mod_scripts[name])), "package.json"), encoding="utf-8"))
                        entry = next(m for m in pkg["map"] if m["name"] == os.path.basename(os.path.dirname(mod_scripts[name])))
                        listed = {x["name"] for x in entry["script"]}
                        self.assertLessEqual({name, name + "_1"}, listed)
                checked += 1
        self.assertGreaterEqual(checked, 10)   # the 7 boxes the PR has, the 3 made
        self.assertEqual(made, 3)

    def test_event_fights_are_bnws(self):
        """The story's battles fight BNW's formations where the SNES pack the event calls holds
        another one in BNW (port_events): each switched fight's party has BNW's line-up and keeps
        the event party's look and battle script, and the mod's copies of its event scripts call
        it (no script left calling the old party)."""
        if MOD_NAME != "BraveNewWorld":
            self.skipTest("BNW's event fights")
        import ff6snes as SN
        import port_encounters as E
        import port_events as PE
        import profiles
        import pr_script as Sc
        import idmap as M
        bnw = SN.Rom(profiles.PROFILES["bnw"].rom)
        parties, van_parties = merged("monster_party"), P.Table("monster_party")
        pf = M.party_formation()
        mod_scripts = {os.path.splitext(os.path.basename(f))[0]: f
                       for f in glob.glob(os.path.join(MOD, "map_*", "Assets", "**", "sc_*.json"), recursive=True)}
        made = [pid for pid in parties.rows if PE.EVENT_PARTY_BASE < pid < PE.EVENT_PARTY_BASE + 1000]
        self.assertGreaterEqual(len(made), 20)
        battles = PE.event_battles()
        for pid in made:
            old = pid - PE.EVENT_PARTY_BASE
            row, tpl = parties[pid], van_parties[old]
            with self.subTest(party=old):
                for col in ("battle_background_asset_id", "battle_bgm_asset_id", "script_name"):
                    self.assertEqual(row[col], tpl[col], col)
                members = E.party_members(row)
                if tpl["not_escape"] == "3":
                    self.assertEqual(row["not_escape"], "3")
                else:   # BNW's monsters' "can't run" (port_escape's rule)
                    norun = any(bnw.data[0x0F0000 + 32 * (m - 1) + 19] & 0x08 for m in members)
                    self.assertEqual(row["not_escape"], "2" if norun else "0")
                self.assertTrue(members)
                self.assertNotEqual(members, E.party_members(tpl), "the same line-up as vanilla's")
                calls = []
                for name in battles.get(old, []):
                    if name not in mod_scripts:
                        continue
                    ms = json.load(open(mod_scripts[name], encoding="utf-8"))["Mnemonics"]
                    calls += [m["operands"]["iValues"][0] for m in ms if m["mnemonic"] in PE.BATTLES]
                self.assertIn(pid, calls, "no event script calls it")
                self.assertNotIn(old, calls, "a script still calls the PR's party")

    def test_added_merchant(self):
        """BNW's Doma Castle merchant (port_merchants, profile added_merchants): an NPC in the PR
        room whose script (a new script.csv row, keyed) calls the plugin's SysCall "BNW:shop <g>"
        for a product group holding BNW's shop 86 stock."""
        if MOD_NAME != "BraveNewWorld":
            self.skipTest("BNW's merchant")
        import ff6snes as SN
        import port_merchants as PM
        import profiles
        rom = SN.Rom(profiles.PROFILES["bnw"].rom)
        sub = os.path.join(MOD, "map_20451", "Assets", "GameAssets", "Serial", "Res", "Map", "Map_20451", "Map_20451_2")
        doc = json.load(open(os.path.join(sub, "entity_default.json"), encoding="utf-8"))
        npcs = [o for l in doc["layers"] for o in (l.get("objects") or [])
                if any(p["name"] == "script_id" and p["value"] > 9900 for p in o.get("properties", []))]
        self.assertEqual(len(npcs), 1)
        props = {p["name"]: p["value"] for p in npcs[0]["properties"]}
        self.assertEqual((npcs[0]["x"] // 16, npcs[0]["y"] // 16 - 1), (55, 10))
        name = merged("script")[props["script_id"]]["script_name"]
        sc = json.load(open(os.path.join(sub, name + ".json"), encoding="utf-8"))
        calls = [m["operands"]["sValues"][0] for m in sc["Mnemonics"] if m["mnemonic"] == "SysCall"]
        self.assertEqual(len(calls), 1)
        self.assertTrue(calls[0].startswith("BNW:shop "))
        group = int(calls[0].split()[1])
        stock = sorted(int(r["content_id"]) for r in merged("product") if r["group_id"] == str(group))
        self.assertEqual(stock, sorted(PM.stock(rom, 86)))
        keys = json.load(open(os.path.join(MOD, "map_20451", "keys", "bnw_npcs.json"), encoding="utf-8"))
        self.assertIn(f"Map_20451_2/{name}", keys["keys"])
        self.assertIn("Map_20451_2/entity_default", keys["keys"])
        # the map loads only the scripts its package.json entry lists
        pkg = json.load(open(os.path.join(os.path.dirname(sub), "package.json"), encoding="utf-8"))
        entry = next(m for m in pkg["map"] if m["name"] == "Map_20451_2")
        self.assertIn({"name": name, "asset": f"Map_20451_2/{name}"}, entry["script"])
        self.assertIn("package", keys["keys"])
        # a player can walk up to it: an open cell beside it connects to the room's entrances
        # (the PR's collision grid: 0 open; entities are bottom-anchored, so the cell is y - 1)
        van_sub = os.path.join(P.VANILLA, os.path.relpath(sub, MOD))
        col = json.load(open(os.path.join(van_sub, "collision.json"), encoding="utf-8"))["layers"][0]
        w, h, cells = col["width"], col["height"], col["data"]
        npc = (npcs[0]["x"] // 16, npcs[0]["y"] // 16 - 1)
        free = lambda c: c != npc and 0 <= c[0] < w and 0 <= c[1] < h and cells[c[1] * w + c[0]] == 0
        seen = {(o["x"] // 16, o["y"] // 16) for l in doc["layers"] for o in (l.get("objects") or [])
                if o.get("name") == "PointIn"}
        self.assertTrue(seen)
        todo = list(seen)
        while todo:
            x, y = todo.pop()
            for c in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
                if c not in seen and free(c):
                    seen.add(c)
                    todo.append(c)
        beside = [(npc[0] + dx, npc[1] + dy) for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))]
        self.assertTrue(any(c in seen for c in beside), f"no way to reach the merchant at {npc}")


    def test_chest_scripts_need_no_wc_plugin(self):
        """The mod's chest scripts (a box's battle, a scripted chest's gift: port_chests) use the
        game's own ops: this mod ships without the Worlds Collide plugin."""
        n = 0
        for path in glob.glob(os.path.join(MOD, "map_*", "Assets", "**", "sc_*.json"), recursive=True):
            ms = json.load(open(path, encoding="utf-8"))["Mnemonics"]
            n += 1
            with self.subTest(file=os.path.relpath(path, MOD)):
                calls = [m["operands"]["sValues"][0] for m in ms if m["mnemonic"] == "SysCall"]
                self.assertFalse([c for c in calls if str(c).startswith("WC:")], "a WC plugin call")
                self.assertNotIn("WC_REWARD", [m["operands"]["sValues"][0] for m in ms if m["mnemonic"] == "Msg"])
        self.assertGreater(n, 5)


def diff_paths(a, b, path=""):
    """Paths where a and b differ, ignoring chest content_id/content_num property values."""
    if type(a) is not type(b):
        return {path}
    if isinstance(a, dict):
        if set(a) != set(b):
            return {path}
        # a chest's contents, and its "Obtained" line, which follows them (port_chests.chest_message)
        if a.get("name") in ("content_id", "content_num", "message_key") and set(a) == {"name", "type", "value"}:
            return set()
        # a monster-in-a-box the mod made a plain chest: its battle script cleared; an item chest
        # the mod made a box: its new box script (port_chests: script ids 9801-9899)
        if a.get("name") == "script_id" and set(a) == {"name", "type", "value"} and (
                b.get("value") == 0 or 9800 < b.get("value") < 9900):
            return set()
        out = set()
        for k in a:
            out |= diff_paths(a[k], b[k], f"{path}.{k}")
        return out
    if isinstance(a, list):
        if len(a) != len(b):
            return {path}
        out = set()
        for i, (x, y) in enumerate(zip(a, b)):
            out |= diff_paths(x, y, f"{path}[{i}]")
        return out
    return set() if a == b else {path}


class UndeadInBattle(unittest.TestCase):
    """A monster is undead in battle by the Undead flag (2) in its monster_flag_group
    (EnemyCharacterParameter's constructor: tools/decomp/undead); the species bit 0 only feeds the
    species list killer weapons read. The porter must move both (WC's -bnu, 2026-11-12: the second
    Behemoth King stayed undead, and the Assassin's Dagger healed it to full on every hit)."""

    @staticmethod
    def undead_groups(table):
        return {int(r["group_id"]) for r in table if r["monster_flag_id"] == "2"}

    def test_vanilla_species_and_flags_agree(self):
        undead = self.undead_groups(P.Table("monster_flag_group"))
        bad = [r["id"] for r in P.Table("monster") if (int(r["species"]) & 1) != (int(r["monster_flag_group_id"]) in undead)]
        self.assertEqual(bad, [])

    def test_flag_group_pairs(self):
        import port
        g, new = P.Table("monster_flag_group"), set()
        self.assertEqual([port.flag_group_with(g, x, 2, False, new) for x in (2, 8, 0, 4, 1)], [0, 4, 0, 4, 1])
        self.assertEqual([port.flag_group_with(g, x, 2, True, new) for x in (0, 4, 2, 8)], [2, 8, 2, 8])
        self.assertEqual(new, set())
        made = port.flag_group_with(g, 1, 2, True, new)   # Libra-proof + undead: no vanilla group has it
        self.assertNotIn(made, range(10))
        self.assertEqual({int(g[i]["monster_flag_id"]) for i in new}, {1, 2})
        self.assertEqual(port.flag_group_with(g, 1, 2, True, set()), made)   # found once made

    def test_ported_monsters_agree(self):
        import port
        import ff6snes as S
        rom = os.path.join(os.path.dirname(PR), "build", "brave-new-world", "Brave New World 3.0 FRC.sfc")
        if not os.path.exists(rom):
            self.skipTest("no BNW ROM")
        tables = {"monster": P.Table("monster")}
        changed, _ = port.port_enemies(S.Rom(port.VANILLA_ROM), S.Rom(rom), tables)
        undead = self.undead_groups(tables["monster_flag_group"])
        bad = [i for i in changed
               if (int(tables["monster"][i]["species"]) & 1) != (int(tables["monster"][i]["monster_flag_group_id"]) in undead)]
        self.assertEqual(bad, [])


@unittest.skipUnless(MOD_NAME == "BraveNewWorld", "BNW-only checks")
class BnwFidelity(unittest.TestCase):
    """Spot checks against Brave New World's manual (Printme 3.0)."""

    def test_terra(self):
        t = merged("character_status")[1]
        self.assertEqual((t["strength"], t["magic"], t["agility"], t["vitality"]), ("30", "42", "30", "30"))
        self.assertEqual((t["defense"], t["ability_defense"], t["evasion_rate"], t["magic_evasion_rate"]), ("30", "36", "6", "12"))

    def test_growth_table(self):
        g = {int(r["lv"]): r for r in merged("growth_curve") if r["group_id"] == "1"}
        hp = mp = 0
        for lv in range(2, 11):
            hp += int(g[lv]["hp_value1"])
            mp += int(g[lv]["mp_value1"])
        self.assertEqual((hp, mp), (140, 9))  # manual: Lv 10 = 140 HP / 9 MP

    def test_ramuh_teaches_bolt_bolt2_demi(self):
        msg = P.messages()
        with open(MOD_MESSAGE, encoding="utf-8") as f:
            msg.update(dict(l.rstrip("\r\n").split("\t", 1) for l in f if "\t" in l))
        content = merged("content")
        taught = sorted(P.strip_tags(msg[content[int(r["content_id"])]["mes_id_name"]])
                        for r in merged("learning") if r["type_id"] == "2" and r["value1"] == "62")
        self.assertEqual(taught, ["Bolt", "Bolt 2", "Demi"])

    def test_esper_rules(self):
        data = json.load(open(os.path.join(MOD, "bnw_data.json"), encoding="utf-8"))
        ramuh = data["espers"][0]
        self.assertEqual(ramuh["name"], "Ramuh")
        self.assertEqual(sorted(ramuh["users"]), [1, 6])  # Locke, Celes
        self.assertEqual([s["sp"] for s in ramuh["spells"]], [1, 15, 10])

    def test_stat_caps_255(self):
        # BNW stops stats at 255 (c2.asm GengiCheckFunction), the PR at 128
        caps = {r["key_name"]: r["value1"] for r in merged("game_constant_int")}
        for key in ("MAX_STRNGTH", "MAX_VITALITY", "MAX_AGILITY", "MAX_MAGIC", "MAX_ABILITY_ATTACK"):
            self.assertEqual(caps[key], "255", key)

    def test_sap_spell(self):
        a = merged("ability")[43]
        self.assertEqual((a["use_value"], a["standard_value"]), ("6", "30"))
        fg = merged("function_group")[int(a["battle_function_group_id"])]
        self.assertIn(("9", "16"), [(fg[f"function{k}_id"], fg[f"function{k}_value1"]) for k in (1, 2, 3)])

    # the PR's weapons that can't be gripped (Serial.FF6.Management.EquipmentAbilityData
    # .InvaliteTwoHandeds, its constructor: tools/decomp/twohand), content ids
    PR_NO_GRIP = {0x7a, 0x9f, 0xa0, 0xa1, 0xa3, 0xa5, 0xa6, 0xa7, *range(0xa9, 0xb8)}

    def test_two_handed_grip_weapons(self):
        """The plugin keeps the game's own grip check (OwnedCharacterData.HasTwohand: ability 251
        and a grippable weapon in one hand, the other empty) and changes only its x2 to BNW's
        x1.5 (RelicRules). That holds while ability 251 comes only with BNW's grip weapons
        (weapon byte 12 bit 3), all of them two-hand capable (byte 19 bit 6) and grippable in
        the PR."""
        import ff6snes as SN
        import idmap as M
        import profiles
        if MOD_NAME != "BraveNewWorld":
            self.skipTest("BNW's weapons")
        rom = SN.Rom(profiles.PROFILES["bnw"].rom)
        fg, content = merged("function_group"), merged("content")
        weapon_content = {int(r["type_value"]): int(r["id"]) for r in content if r["type_id"] == "2"}

        def gives_grip(row):
            g = fg.rows.get(int(row["wear_function_group_id"]))
            return bool(g) and ("62", "251") in [(g[f"function{k}_id"], g[f"function{k}_value1"]) for k in (1, 2, 3)]
        ported = {weapon_content[int(r["id"])] for r in merged("weapon") if gives_grip(r)}
        armor_grip = [r["id"] for r in merged("armor") if gives_grip(r)]
        bnw = {M.item_content(it.id) for it in SN.items(rom) if (it.raw[0] & 7) == 1 and it.raw[12] >> 3 & 1}
        capable = {M.item_content(it.id) for it in SN.items(rom) if (it.raw[0] & 7) == 1 and it.raw[19] >> 6 & 1}
        self.assertEqual(len(bnw), 15, "BNW's 7 spears, 7 katanas, Excalibur")
        self.assertEqual(ported, bnw, "weapons giving ability 251")
        # the PR's Gauntlet (armor 111) keeps vanilla's grip, but BNW has no Gauntlet: its slot
        # (SNES item 208) is a blank item, so nothing gives armor 111 in BNW
        self.assertLessEqual(set(armor_grip), {"111"}, "armor giving the grip")
        self.assertNotEqual(SN.items(rom)[208].raw[0] & 7, 5, "BNW's Gauntlet slot is no relic")
        self.assertLessEqual(bnw, capable, "every grip weapon is two-hand capable")
        self.assertFalse(bnw & self.PR_NO_GRIP, "none is on the PR's no-grip list")

    def test_true_knight_items(self):
        """The plugin's cover (CoverRules) finds a knight by condition type 606 (TipsForKnights:
        the PR's condition 29); every BNW item with the True Knight bit (relic byte 12 bit 6,
        weapons too) gives it, and no other item does."""
        import ff6snes as SN
        import idmap as M
        import profiles
        if MOD_NAME != "BraveNewWorld":
            self.skipTest("BNW's items")
        rom = SN.Rom(profiles.PROFILES["bnw"].rom)
        cond, cg = merged("condition"), merged("condition_group")
        knight_conditions = {i for i, r in cond.rows.items() if r["condition_type"] == "606"}
        self.assertEqual(knight_conditions, {29})
        groups = {int(r["group_id"]) for r in cg if int(r["condition_id"]) in knight_conditions}
        gives = set()
        for kind in ("weapon", "armor"):
            for r in merged(kind):
                if int(r["wear_condition_group_id"]) in groups:
                    gives.add((kind, int(r["id"])))
        want = {M.item(it.id) for it in SN.items(rom) if it.raw[12] >> 6 & 1 and M.item(it.id)}
        self.assertGreater(len(want), 0)
        self.assertEqual(sorted(gives), sorted(want), "items giving the knight's condition")

    def test_no_level_spell_for_everyone(self):
        """No learning row teaches someone it shouldn't: a level row (type 1) at job 0 is "every
        character" to the game, and a magicite row (type 2) at value1 0 is "no magicite"
        (MagicStoneData.GetLearnableAbilityList matches type 2 and value1 = the equipped magicite,
        0 without one), an equipment row (type 4) at value1 0 an empty slot. The port parks
        leftover rows at type 0, which no lookup reads (a new BNW game once learned ~12 spells
        after its first battle from rows parked at level 0; Biggs and Wedge then got 19 spells'
        entries from magicite rows parked at value1 0)."""
        rows = list(merged("learning"))
        bad = [(r["id"], r["value1"], r["content_id"]) for r in rows
               if r["type_id"] == "1" and r["job_id"] == "0" and int(r["value1"]) <= 99]
        self.assertEqual(bad, [], "level rows every character would learn")
        empty = [(r["id"], r["type_id"], r["content_id"]) for r in rows if r["type_id"] in ("2", "4") and r["value1"] == "0"]
        self.assertEqual(empty, [], "magicite / equipment rows matching a character without one")
        known = {"0", "1", "2", "3", "4"}
        self.assertEqual(sorted({r["type_id"] for r in rows} - known), [], "a learning type the port doesn't know")

    def test_event_abilities_untouched(self):
        """The PR's battle-event abilities (type 35) are the PR's: 696 "do nothing" (the AIs'
        idle turn), 735 SM_ERASER (everyone vanishes, the battle ends), 738 / 776 (the Whelk's
        head shown / hidden)... The special-attack port once took them for monsters' specials
        (377 + monster index) and made them attacks: idle monsters hit themselves, and the
        Whelk's shell armed its counter on its own and never brought the head back."""
        van = P.Table("ability")
        mod = merged("ability")
        changed = [a for a, r in van.rows.items() if r["type_id"] == "35" and mod[a] != r]
        self.assertEqual(changed, [], "battle-event abilities the mod changed")

    def test_specials_follow_their_monsters(self):
        """Each monster BNW gave another special attack gets it on the PR ability that carries
        its own special (port_specials.pr_specials: the type-3 ability its AI / Control group
        uses), rebuilt in place only when every monster sharing that ability gets the same
        byte, else on a copy; abilities that aren't specials are never rewritten."""
        sys.path.insert(0, os.path.join(PR, "tools"))
        import ff6snes as S
        import port_specials as PS
        import profiles
        van, bnw = S.Rom(P.VANILLA_ROM), S.Rom(profiles.PROFILES["bnw"].rom)
        specials = PS.pr_specials()
        owner, writes = PS.plan(van, bnw)
        ve, be = S.enemies(van), S.enemies(bnw)
        ab, fg = merged("ability"), merged("function_group")
        sharers = defaultdict(list)
        for e, a in specials.items():
            sharers[a].append(e)
        for aid, (template, byte, who) in writes.items():
            self.assertIn(template, sharers, f"{aid}: written from a non-special {template}")
            if aid == template:
                self.assertEqual(sorted(who), sorted(sharers[aid]), f"{aid} rebuilt in place for only some of its monsters")
            funcs, system, _, _ = PS.build(byte)
            g = fg[int(ab[aid]["battle_function_group_id"])] if ab[aid]["battle_function_group_id"] != "0" else None
            got = PS.FunctionGroups.funcs(g) if g else frozenset()
            self.assertEqual((got, ab[aid]["system_id"]), (frozenset(funcs), system), f"ability {aid} for monsters {who}")
        for e, aid in owner.items():
            if ve[e].raw[31] != be[e].raw[31] and PS.build(be[e].raw[31]) is not None and not S.blank_enemy(bnw, e):
                self.assertIn(aid, writes, f"monster {e}'s BNW special isn't written")
        # every ability the mod changed in the specials' range is a special or a copy
        van_ab = P.Table("ability")
        touched = {a for a in range(377, 761) if a in van_ab.rows and ab[a] != van_ab[a]}
        self.assertEqual(sorted(touched - set(sharers)), [], "non-special abilities changed")

    def test_whelk_is_bnws(self):
        """BNW's Whelk (enemy 256/308 scripts, monstBattleScripts.txt): the shell opens with the
        Biggs/Wedge talk (the PR's battle script plays it), brings the head out and then only
        counters any attack with Discharge (B7 -> PR 320); the head attacks, and its death makes
        every monster vanish (735). Not the PR's head-in/head-out routine (BNW's Chesticle has
        that one), which hid the head for good once 738 was broken (2026-11-07)."""
        mo = merged("monster")
        names = {int(r["id"]): r["script_name"] for r in merged("monster_ai_script")}

        def acts(mid):
            name = names[int(mo[mid]["script_id"])]
            path = os.path.join(MOD_AI, name + ".json")
            doc = json.load(open(path if os.path.exists(path) else os.path.join(VAN_AI, name + ".json"), encoding="utf-8"))
            out = defaultdict(set)
            for m in doc["Mnemonics"]:
                if m["mnemonic"] in ("Act", "CounterAct", "FinalAttackAct", "ActForTarget"):
                    out[m["mnemonic"]] |= set(m["operands"]["iValues"] if m["mnemonic"] != "ActForTarget" else m["operands"]["iValues"][2:3])
            return out

        shell, head = acts(257), acts(309)
        self.assertEqual(shell["CounterAct"], {320}, "the shell counters with Discharge")
        self.assertEqual(shell["Act"] - {1}, {696}, "the shell idles (attacks only when confused)")
        self.assertFalse(shell["ActForTarget"], "the shell doesn't hide or show the head")
        self.assertEqual(head["FinalAttackAct"], {735}, "the head's death ends the fight")
        self.assertEqual(head["Act"], {1})

    def test_magitek_moves_stay(self):
        """The Magitek command lists ability group 7 (the PR's 8 Magitek moves, type 604). BNW used
        the SNES MagiTek slots 131-138 for other spells; those go to abilities of their own
        (profiles.py spell_relocations), so the Magitek moves stay the PR's."""
        van, ab = P.Table("ability"), merged("ability")
        for k in range(218, 226):
            with self.subTest(ability=k):
                self.assertEqual(ab[k], van[k], "a Magitek move changed")
        group7 = sorted(int(r["id"]) for r in ab if r["ability_group_id"] == "7")
        self.assertEqual(group7, list(range(218, 226)), "the Magitek command's moves")

    def test_dance_descriptions(self):
        """BNW's skill menu shows each dance's four moves: each dance's description lists the
        names of its four steps, as the mod's text names them (port_dances.describe_steps)."""
        import ff6snes as SN
        import port_dances as D
        import profiles
        if MOD_NAME != "BraveNewWorld":
            self.skipTest("BNW's dances")
        rom = SN.Rom(profiles.PROFILES["bnw"].rom)
        msg = P.messages()
        with open(MOD_MESSAGE, encoding="utf-8") as f:
            msg.update(dict(l.rstrip("\r\n").split("\t", 1) for l in f if "\t" in l))
        content = merged("content")
        key = {(r["type_id"], int(r["type_value"])): r for r in content}
        for k, steps in enumerate(D.steps(rom)):
            with self.subTest(dance=k):
                dance = key[("4", 164 + k)]
                want = " / ".join(msg[key[("4", a)]["mes_id_name"]] for a in steps)
                self.assertEqual(msg[dance["mes_id_description"]], want)

    def test_shock(self):
        """Shock (PR ability 19, the Shock command's; BNW's !Shock): ignores defense (content flag
        5, profiles.py ability_overrides); the plugin computes its damage and cost. The
        Windslash / Aero procs (byte 7 bit 3) are listed for the plugin's grip factor."""
        if MOD_NAME != "BraveNewWorld":
            self.skipTest("BNW's Shock")
        a = merged("ability")[19]
        self.assertEqual(a["type_id"], "618")
        flags = {int(r["content_flag_id"]) for r in merged("content_flag_group") if r["group_id"] == a["content_flag_group_id"]}
        self.assertEqual(flags, {1, 5}, "vanilla's flag 1 + ignore defense")
        data = json.load(open(os.path.join(MOD, "bnw_data.json"), encoding="utf-8"))
        self.assertLessEqual({153, 154}, set(data["spirit"]["weapon_flag_abilities"]), "!WindSlash, !Aero")


if __name__ == "__main__":
    unittest.main()
