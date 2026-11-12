"""Worlds Collide on the SNES and in the PR, one seed: a few things cross-checked (minimal).

One seed and flag set ("xref": the moogle cup preset plus WC's spoiler log, -sl) is made twice:
  - by WC's own wc.py on the vanilla ROM: the SNES game. Its ROM and spoiler log stay in the
    test's temp folder and are deleted after (testing only: the mod never hands out a ROM)
  - by our generator (tools/wc_seed.py), ROM-free as it ships: the PR seed folder the plugin
    applies
and each test compares one thing between the two. The SNES side is read at WC's own table
addresses (mods/worlds-collide/data); SNES ids meet PR ids through tools/idmap.py (checked
against the vanilla game by tools/check_vanilla.py) or by name:
  hash       the same WC game on both sides (WC's hash)
  rewards    every check's reward: WC's spoiler log (Events) = the PR seed's checks, by name
  commands   every character's commands: the SNES character data = character_status, by name
  espers     every esper's spells, learn rates and level-up bonus
  shops      every SNES shop's stock = its PR shop's (the porter's shop pairs)
  chests     every chest WC changed = its PR chest(s) (the porter's chest pairs)
  start      the SNES game after New Game, run in Mesen 2 headless (party, gil, items,
             espers, levels) = the PR seed's start; skipped without Mesen (tools/mesen2)

  pr/.venv/Scripts/python -m unittest discover -s pr/tests -p "test_wc_xref.py" -v
"""
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import warnings

HERE = os.path.dirname(os.path.abspath(__file__))
PR = os.path.dirname(HERE)
TOOLS = os.path.join(PR, "tools")
WC = os.path.join(PR, "..", "mods", "worlds-collide")
sys.path.insert(0, TOOLS)
import ff6snes as SN  # noqa: E402
import idmap as M  # noqa: E402
import prdata as P  # noqa: E402
import wc_live as L  # noqa: E402

SEED, PRESET, EXTRA = "xref", "moogle cup", "-sl"
# any other seed (a player's): FF6_XREF_SEED and FF6_XREF_FLAGS, its whole flag string (no preset)
if os.environ.get("FF6_XREF_SEED"):
    SEED, PRESET, EXTRA = os.environ["FF6_XREF_SEED"], None, (os.environ.get("FF6_XREF_FLAGS", "") + " -sl").strip()

# WC's table addresses (unheadered ROM offsets; mods/worlds-collide/data/*.py)
CHAR_INIT, CHAR_INIT_SIZE = 0x2D7CA0, 22        # characters.py INIT_DATA_START: commands at [2:6]
CHAR_NAMES, CHAR_NAME_SIZE = 0x0478C0, 6        # characters.py NAMES_START
ESPER_DATA, ESPER_SIZE = 0x186E00, 11           # espers.py SPELLS_BONUS_DATA_START: (rate, spell) x5, bonus
ESPER_NAMES, ESPER_NAME_SIZE = 0x26F6E1, 8      # espers.py NAMES_START
ITEM_NAMES, ITEM_NAME_SIZE = 0x12B300, 13       # items.py NAMES_START (an icon, then the name)
SHOPS, SHOP_SIZE, SHOP_COUNT = 0x47AC0, 9, 128  # shops.py DATA_START: type, 8 items (0xFF: none)

# FF3us command names (WC's constants/commands.py) -> the PR's (command.csv names). Morph is
# the PR's command 9 ("Revert" by name: the one the PR's own Terra has, and it trances)
PR_COMMAND = {"Fight": "Attack", "Item": "Items", "Magic": "Magic", "Morph": "Revert", "Revert": "Revert",
              "Steal": "Steal", "Capture": "Mug", "SwdTech": "Bushido", "Throw": "Throw", "Tools": "Tools",
              "Blitz": "Blitz", "Runic": "Runic", "Lore": "Lore", "Sketch": "Sketch", "Control": "Control",
              "Slot": "Slot", "Rage": "Rage", "Leap": "Leap", "Mimic": "Mimic", "Dance": "Dance", "Row": "Row",
              "Def": "Defend", "Jump": "Jump", "X Magic": "Dualcast", "GP Rain": "Gil Toss", "Health": "Pray",
              "Shock": "Shock", "Possess": "Possess", "MagiTek": "Magitek"}
# every character has these where it has them at all (the PR shows Magic once a spell is
# known, and every PR character keeps a hidden Magitek slot for the armor): not compared
COMMON = {"Attack", "Items", "Magic"}
# SNES chests (WC's chest ids) with no PR chest: test_chests (none since 2026-11-06: the Phantom
# Train's are the PR's pick-ups, port_chests.MERGED)
NO_PR_CHEST = set()


# Mesen 2 (headless test runner: no window, no sound, no input devices; github.com/SourMesen/Mesen2):
# FF6_MESEN, else tools/mesen2/Mesen.exe in the project (a settings.json next to it keeps Mesen
# portable, which also skips its first-run setup window, and lets the Lua script write its file)
MESEN = os.environ.get("FF6_MESEN") or os.path.join(PR, "..", "tools", "mesen2", "Mesen.exe")
# After New Game, WC's start event sets the game up and puts the party on the Blackjack's deck
# (SNES map 6: event/start.py start_game_mod). The script presses A through the title and New
# Game until then, waits 3 s, and writes what the game holds (WRAM: $1600 + 37n character
# slot n, $1850 + n its party bits, $1860 gil, $1869/$1969 items and counts, $1A69 espers,
# $1F64 the map). FF6's own layout, as WC's event code writes it.
SNES_START_LUA = r'''
local out = [[%OUT%]]
local frame, deck, done = 0, nil, false
local function rd(a) return emu.read(a, emu.memType.snesWorkRam) end
local function map() return (rd(0x1F64) + rd(0x1F65) * 256) & 0x1FF end
emu.addEventCallback(function()
  emu.setInput({ a = (deck == nil and frame > 120 and frame % 16 < 2) }, 0)
end, emu.eventType.inputPolled)
emu.addEventCallback(function()
  frame = frame + 1
  if done then return end
  if deck == nil and map() == 6 then deck = frame end
  if (deck ~= nil and frame - deck > 180) or frame > 60 * 240 then
    done = true
    local s = {}
    s[#s + 1] = '"frame": ' .. frame .. ', "map": ' .. map()
    s[#s + 1] = '"gil": ' .. (rd(0x1860) + rd(0x1861) * 256 + rd(0x1862) * 65536)
    local ch = {}
    for n = 0, 15 do
      local b = 0x1600 + 37 * n
      local function w(a) return rd(a) + rd(a + 1) * 256 end
      ch[#ch + 1] = string.format('[%d, %d, %d, %d, [%d, %d, %d, %d], [%d, %d, %d, %d]]', n, rd(b), rd(0x1850 + n), rd(b + 8),
        rd(b + 0x16), rd(b + 0x17), rd(b + 0x18), rd(b + 0x19), w(b + 9), w(b + 0x0B) & 0x3FFF, w(b + 0x0D), w(b + 0x0F) & 0x3FFF)
    end
    s[#s + 1] = '"slots": [' .. table.concat(ch, ', ') .. ']'
    local it = {}
    for i = 0, 255 do
      local id, q = rd(0x1869 + i), rd(0x1969 + i)
      if id ~= 0xFF and q > 0 then it[#it + 1] = string.format('[%d, %d]', id, q) end
    end
    s[#s + 1] = '"items": [' .. table.concat(it, ', ') .. ']'
    local es = {}
    for e = 0, 26 do
      if (rd(0x1A69 + (e >> 3)) >> (e & 7)) & 1 == 1 then es[#es + 1] = tostring(e) end
    end
    s[#s + 1] = '"espers": [' .. table.concat(es, ', ') .. ']'
    local f = io.open(out, "w")
    f:write('{' .. table.concat(s, ', ') .. '}')
    f:close()
    emu.stop(deck ~= nil and 0 or 3)
  end
end, emu.eventType.endFrame)
'''


def wc_module(rel):
    """A WC data module that is plain data (constants/*.py), loaded by path."""
    spec = importlib.util.spec_from_file_location("wc_" + rel.replace("/", "_"), os.path.join(WC, rel + ".py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def run(cmd, cwd, **env):
    return subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace",
                          env={**os.environ, **env} if env else None)


def made_seed(tmp):
    """(SNES ROM path, WC's stdout, PR seed folder) for SEED: WC's wc.py and our generator."""
    flags = json.load(open(os.path.join(PR, "..", "mods", "worlds-collide-presets.json"), encoding="utf-8"))[PRESET] if PRESET else ""
    rom = os.path.join(tmp, "snes", "wc.sfc")
    os.makedirs(os.path.dirname(rom))
    p = run([sys.executable, "wc.py", "-i", P.VANILLA_ROM, "-o", rom, "-s", SEED] + flags.split() + EXTRA.split(), WC)
    if p.returncode != 0 or not os.path.exists(rom):
        raise unittest.SkipTest(f"WC didn't run: {(p.stderr or p.stdout)[-300:]}")
    seed = os.path.join(tmp, "seed")
    # ours as it ships: ROM-free (tools/wc_basedata.py's data, no ROM read or written)
    g = run([sys.executable, os.path.join(TOOLS, "wc_seed.py"), "--seed", SEED] + (["--preset", PRESET] if PRESET else []) +
            ["--flags=" + EXTRA, "--out", seed], PR, FF6_WC_ROMFREE="1")
    if g.returncode != 0:
        raise AssertionError(f"the generator failed: {(g.stderr or g.stdout)[-600:]}")
    return rom, p.stdout, seed


class WcXref(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        warnings.simplefilter("ignore", ResourceWarning)
        if not os.path.exists(P.VANILLA_ROM):
            raise unittest.SkipTest("no vanilla ROM")
        cls.tmp = tempfile.mkdtemp(prefix="wc_xref_")
        try:
            rom, cls.wc_out, cls.seed = made_seed(cls.tmp)
        except Exception:
            shutil.rmtree(cls.tmp, ignore_errors=True)
            raise
        cls.rom_path = rom
        cls.rom, cls.van = SN.Rom(rom), SN.Rom(P.VANILLA_ROM)
        with open(os.path.splitext(rom)[0] + ".txt", encoding="utf-8") as f:
            cls.log = f.read()
        with open(os.path.join(cls.seed, "wc_seed.json"), encoding="utf-8") as f:
            cls.info = json.load(f)
        cls.system = P.messages("system", "en")

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    # --- helpers ---------------------------------------------------------------------------
    def table(self, name):
        """The seed's full master table (the plugin applies it as is)."""
        with open(os.path.join(self.seed, *L.MASTER_PATH.split("/"), name + ".csv"), encoding="utf-8", newline="") as f:
            return P.Table(name, f.read())

    def snes_name(self, start, size, i):
        return SN.decode_name(self.rom.b(start + i * size, size))

    def section(self, title):
        """A section of WC's spoiler log (the lines under its ---- title ---- rule)."""
        m = re.search(r"^-+ " + re.escape(title) + r" -+\n(.*?)(?=^-{20,} |\Z)", self.log, re.M | re.S)
        self.assertIsNotNone(m, f"WC's log has no {title} section")
        return m.group(1).splitlines()

    def pr_reward_name(self, kind, pr_id):
        """A PR seed's reward named as WC names it (the SNES ROM's own name of that id)."""
        if kind == "character":
            return self.snes_name(CHAR_NAMES, CHAR_NAME_SIZE, pr_id - 1)
        if kind == "esper":
            e = next(e for e in range(27) if M.esper_content(e) == pr_id)
            return self.snes_name(ESPER_NAMES, ESPER_NAME_SIZE, e)
        i = next(i for i in range(255) if M.item_content(i) == pr_id)
        return self.snes_name(ITEM_NAMES, ITEM_NAME_SIZE, i)

    # --- the tests -------------------------------------------------------------------------
    def test_same_game(self):
        """Both sides are one WC game: WC's hash for the seed and flags."""
        snes_hash = re.search(r"^Hash\s+(.+)$", self.wc_out, re.M).group(1).strip()
        self.assertEqual(self.info["hash"], snes_hash)

    def test_check_rewards(self):
        """Every check's reward(s), WC's spoiler log against the PR seed (by name; the PR seed
        holds PR ids): the user's playtest found "Celes" at South Figaro was Relm."""
        logged = {}
        for line in self.section("Events"):
            if not line.strip() or line.startswith(" "):
                continue                                   # a boss swap or an auction line
            name, rest = line[:30].rstrip(), line[30:].strip()
            rest = re.sub(r"\s*\([^)]*\)", "", rest)     # prices (auction, Tzen)
            logged[name] = sorted(r.strip() for r in rest.split(",") if r.strip())
        self.assertGreater(len(logged), 40)
        ours = {c["event"]: sorted(self.pr_reward_name(k, i) for k, i in c["rewards"]) for c in self.info["checks"]}
        start = sorted(self.pr_reward_name("character", i) for i in self.info["start"]["characters"])
        bad = []
        for event, rewards in logged.items():
            got = start if event == "Start" else ours.get(event)
            if got != rewards:
                bad.append(f"{event}: SNES {rewards}, PR {got}")
        self.assertEqual(bad, [])

    def test_commands(self):
        """Every character's commands (SNES character data -> character_status), by name."""
        snes_names = wc_module("constants/commands").id_name
        cs = self.table("character_status")
        pr_names = {r["id"]: self.system.get(r["mes_id_name"], "") for r in P.Table("command")}
        bad = []
        for c in range(14):
            raw = self.rom.b(CHAR_INIT + c * CHAR_INIT_SIZE + 2, 4)
            snes = {PR_COMMAND.get(snes_names[x], snes_names[x]) for x in raw if x != 0xFF} - COMMON
            row = cs[c + 1]
            pr = [pr_names.get(row[f"command_id{k}"], row[f"command_id{k}"]) for k in range(1, 7) if row[f"command_id{k}"] != "0"]
            if "Magitek" not in snes and "Magitek" in pr:
                pr.remove("Magitek")                       # the PR's hidden armor slot
            if snes != set(pr) - COMMON:
                bad.append(f"{self.snes_name(CHAR_NAMES, CHAR_NAME_SIZE, c)}: SNES {sorted(snes)}, PR {sorted(set(pr) - COMMON)}")
        self.assertEqual(bad, [])

    def test_espers(self):
        """Every esper's spells and learn rates (learning.csv) and its level-up bonus."""
        content = P.Table("content")
        by_ability = {int(r["type_value"]): int(r["id"]) for r in content if r["type_id"] == "4"}
        learning = self.table("learning")
        # the seed lists only the bonuses it changed; the rest stay vanilla's (the PR's own: the
        # plugin answers just the changed ones, WcEspers). The user's seed (-ebr 100) gave Ifrit
        # and Siren their vanilla bonuses back
        bonus = dict((k, v) for k, v in self.info["esper_bonus"]["vanilla"])
        bonus.update((k, v) for k, v in self.info["esper_bonus"]["seed"])
        bad = []
        for e in range(27):
            raw = self.rom.b(ESPER_DATA + e * ESPER_SIZE, ESPER_SIZE)
            snes = sorted((by_ability[M.spell(raw[2 * k + 1])], raw[2 * k]) for k in range(5) if raw[2 * k + 1] != 0xFF)
            esper = M.esper_content(e)
            pr = sorted((int(r["content_id"]), int(r["value2"])) for r in learning
                        if r["type_id"] == "2" and int(r["value1"]) == esper)
            name = self.snes_name(ESPER_NAMES, ESPER_NAME_SIZE, e)
            if snes != pr:
                bad.append(f"{name} spells: SNES {snes}, PR {pr}")
            if bonus.get(esper) != raw[10]:
                bad.append(f"{name} bonus: SNES {raw[10]}, PR {bonus.get(esper)}")
        self.assertEqual(bad, [])

    def test_battle_backgrounds(self):
        """Every battle shows its place's background, as the SNES takes it from the map: an area
        the seed refilled fights on its vanilla background and music (the user's seed fought a
        factory formation in the Crumbling House on the factory's background, to battle music the
        house doesn't play), a swapped event battle on the event's background (its music the
        formation's) unless an enemy's AI asks for that party by id."""
        ms, mp = self.table("monster_set"), self.table("monster_party")
        vms, vmp = P.Table("monster_set"), P.Table("monster_party")

        def look(r):
            return r["battle_background_asset_id"], r["battle_bgm_asset_id"]
        bad = []
        for sid, row in ms.rows.items():
            vrow = vms.rows.get(sid)
            if not vrow or all(row[f"monster_set{k}"] == vrow[f"monster_set{k}"] for k in range(1, 5)):
                continue
            want = look(vmp[int(vrow["monster_set1"])])
            bad += [f"set {sid}: party {p} {look(mp[p])}, the area's {want}" for p in
                    (int(row[f"monster_set{k}"]) for k in range(1, 5)) if p and look(mp[p]) != want]
        checked = set()
        for root, _, files in os.walk(os.path.join(self.seed, "files")):
            for fn in files:
                text = open(os.path.join(root, fn), encoding="utf-8").read()
                checked |= {int(x) for x in re.findall(r'"IsCheckMonsterPTId","operands":\{"iValues":\[(\d+)', text)}
        for old, targets in self.info.get("battles", {}).items():
            if int(old) not in vmp.rows:
                continue                                   # not a vanilla party: nothing to keep
            for t in targets:
                if t in mp.rows and t not in checked and \
                        mp[t]["battle_background_asset_id"] != vmp[int(old)]["battle_background_asset_id"]:
                    bad.append(f"event party {old} -> {t}: background {mp[t]['battle_background_asset_id']}, "
                               f"the event's {vmp[int(old)]['battle_background_asset_id']}")
        self.assertEqual(bad[:12], [], f"{len(bad)} battles away from their place's background")

    def test_shops(self):
        """Every SNES shop's stock = its PR shop's (the porter's pairs: the PR keeps a few
        shops twice, both get the stock)."""
        import port
        port.port_shops(self.van, self.rom, {})
        pairs = port.port_shops.pairs
        self.assertGreater(len(pairs), 60)
        product = self.table("product")
        by_group = {}
        for r in product:
            by_group.setdefault(int(r["group_id"]), []).append(int(r["content_id"]))
        bad = []
        for s, g in pairs:
            raw = self.rom.b(SHOPS + s * SHOP_SIZE, SHOP_SIZE)
            snes = sorted(M.item_content(x) for x in raw[1:] if x != 0xFF)
            if snes != sorted(by_group.get(g, [])):
                bad.append(f"SNES shop {s} -> PR {g}: SNES {snes}, PR {sorted(by_group.get(g, []))}")
        self.assertEqual(bad, [])

    def snes_start(self):
        """The SNES game after New Game, run in Mesen 2 headless (SNES_START_LUA)."""
        if not os.path.exists(MESEN):
            self.skipTest(f"no Mesen 2 ({MESEN})")
        work = os.path.join(self.tmp, "mesen")
        os.makedirs(work, exist_ok=True)
        out, lua = os.path.join(work, "start.json"), os.path.join(work, "start.lua")
        with open(lua, "w", encoding="utf-8") as f:
            f.write(SNES_START_LUA.replace("%OUT%", out))
        saves = os.path.join(os.path.dirname(MESEN), "Saves")
        for fn in (os.listdir(saves) if os.path.isdir(saves) else []):
            if fn.lower().startswith("wc."):
                os.remove(os.path.join(saves, fn))          # a fresh SRAM: New Game, no Continue
        p = subprocess.run([MESEN, "--testRunner", "--timeout=120", lua, self.rom_path],
                           capture_output=True, text=True, timeout=180)
        self.assertTrue(os.path.exists(out), f"Mesen wrote nothing (exit {p.returncode}) {p.stdout[-300:]} {p.stderr[-300:]}")
        with open(out, encoding="utf-8") as f:
            state = json.load(f)
        self.assertEqual((p.returncode, state["map"]), (0, 6), f"not on the Blackjack's deck after New Game: {state}")
        return state

    def test_start_in_the_snes_game(self):
        """WC's start as the SNES game runs it (party, gil, items, espers, levels) = the PR seed's
        (the plugin's WC:start gives what wc_seed.json "start" holds; the PR's levels are
        character_status lv)."""
        state = self.snes_start()
        start = self.info["start"]
        party = sorted(n + 1 for n, actor, bits, lv, cmds, hpmp in state["slots"] if n < 14 and bits & 7 == 1)
        self.assertEqual(party, sorted(start["characters"]), "the start party (PR character ids)")
        self.assertEqual(state["gil"], start["gold"])
        self.assertEqual(sorted([M.item_content(i), q] for i, q in state["items"]), sorted(start["items"]), "the start items")
        self.assertEqual(sorted(M.esper_content(e) for e in state["espers"]), sorted(start.get("espers", [])), "the start espers")
        cs = self.table("character_status")
        levels = {n + 1: lv for n, actor, bits, lv, cmds, hpmp in state["slots"] if n + 1 in party}
        self.assertEqual(levels, {c: int(cs[c]["lv"]) for c in party}, "the start party's levels")
        # HP and MP: the SNES start has every character full, at the HP/MP the PR table gives
        # (the user's Deck seed, 2026-11-06: current HP under max in the PR; the plugin's
        # in-game check covers the PR's own join extras, WcEvents.FreshRecruit)
        hpmp = {n + 1: hm for n, actor, bits, lv, cmds, hm in state["slots"] if n < 14}
        self.assertEqual({c: (hm[0], hm[2]) for c, hm in hpmp.items()}, {c: (hm[1], hm[3]) for c, hm in hpmp.items()},
                         "the SNES start: everyone at full HP and MP")
        self.assertEqual({c: (hpmp[c][1], hpmp[c][3]) for c in party}, {c: (int(cs[c]["hp"]), int(cs[c]["mp"])) for c in party},
                         "the start party's max HP and MP = the PR table's")

    def test_check_npcs(self):
        """Every check NPC WC re-skins (its SNES output ROM's NPC table) shows that art in the PR
        seed's copy of its map (tools/wc_npcs.py pairs; the user's playtest: "Celes" freed at
        South Figaro was Relm and should have looked like her)."""
        import wc_npcs
        rom, van = self.rom.data, self.van.data
        PTRS, DATA = 0x41A10, 0x41D52                 # WC data/maps.py NPCS_PTR_START, npcs.py DATA_START

        def sprite(d, m, n):
            ptr = lambda k: d[PTRS + 2 * k] | d[PTRS + 2 * k + 1] << 8
            return d[DATA + 9 * ((ptr(m) - ptr(0)) // 9 + n - 0x10) + 6]
        flags = self.info["flags"].split()
        seed_files = set(self.info["files"])
        checked, bad = 0, []
        for (m, n), pairs in sorted(wc_npcs.PAIRS.items()):
            s = sprite(rom, m, n)
            if s == sprite(van, m, n):
                continue                                   # not re-skinned in this seed
            want = wc_npcs.asset_for(s, flags)
            if want is None:
                continue                                   # a sprite with no PR art (kept)
            for rel, obj in pairs:
                path = os.path.join(self.seed, "files", *rel.split("/")) if rel in seed_files else os.path.join(P.VANILLA, *rel.split("/"))
                doc = json.load(open(path, encoding="utf-8"))
                got = next((p["value"] for layer in doc.get("layers", []) for o in layer.get("objects", []) or []
                            if o["id"] == obj for p in o.get("properties", []) if p["name"] == "asset_id"), None)
                checked += 1
                if got != want:
                    bad.append(f"SNES map {m} NPC {n:#x} sprite {s}: PR {rel.split('/')[-2]}/{rel.split('/')[-1]} #{obj} shows {got}, not {want}")
        self.assertGreater(checked, 5)
        self.assertEqual(bad, [])

    def test_chests(self):
        """Every item and gil chest WC changed = its PR chest(s) in the seed (the porter's
        chest pairs; a PR map the seed doesn't carry is the vanilla one), and every
        monster-in-a-box WC gave other monsters (-cms, both presets) fights them in the PR box,
        on that box's own background."""
        import port_chests as PC
        import port_encounters as E
        import pr_script as PS
        vc, wc = PC.snes_chests(self.van), PC.snes_chests(self.rom)
        # (a box whose pack WC changed in place, -fer, is changed too)
        changed = {k for k, c in wc.items() if not vc.get(k) or not PC.same_chest(vc[k], c)}
        flags = PC.match_flags(vc, changed=changed)
        # the plugin leaves the boxes' battles to the port: the seed names all 11 boxes' scripts
        self.assertEqual(self.info.get("boxes"), PC.box_scripts(flags, vc))
        self.assertEqual(len(self.info["boxes"]), 11)
        docs = {}

        def gifts(e):
            """What a scripted chest gives: the seed's (port_chests.fill_empty), else vanilla's."""
            path = os.path.join(self.seed, "files", e["empty"])
            if not os.path.exists(path):
                kind, val = e["key"]
                return [] if (kind, val) == ("gil", 0) else [(1, val) if kind == "gil" else (val, 1)]
            out = []
            for m in json.load(open(path, encoding="utf-8"))["Mnemonics"]:
                call = m["operands"]["sValues"][0].split() if m["mnemonic"] == "SysCall" else []
                if call[:1] == ["WC:item"]:
                    out.append((int(call[1]), int(call[2])))
                elif call[:1] == ["WC:gil"]:
                    out.append((1, int(call[1])))
            return out

        def chest(e):
            if e.get("empty"):
                # the PR's scripted chest (empty, or a pick-up): its script gives what the seed put in
                g = gifts(e)
                return g[0] if g else (1, 0)
            rel = os.path.relpath(e["file"], P.VANILLA)
            path = os.path.join(self.seed, "files", rel)
            if rel not in docs:
                with open(path if os.path.exists(path) else e["file"], encoding="utf-8") as f:
                    docs[rel] = {o["id"]: {x["name"]: x["value"] for x in o.get("properties", [])}
                                 for layer in json.load(f).get("layers", []) for o in layer.get("objects", []) or []}
            p = docs[rel][e["obj"]]
            return p.get("content_id"), p.get("content_num")
        mp, vmp = self.table("monster_party"), P.Table("monster_party")

        def battle(name, folder):
            path = os.path.join(folder, *PS.rel(name).split("/"))
            if not os.path.exists(path):
                return None
            return next((m["operands"]["iValues"][0] for m in json.load(open(path, encoding="utf-8"))["Mnemonics"]
                         if m["mnemonic"] in PC.BATTLES), None)
        # a formation's monsters as the PR fights them in a box: its party, or the PR's own party
        # for a vanilla box of that formation (Narshe's Lobos: 516)
        own = {}
        for k, c in vc.items():
            if c["type"] == PC.MONSTER:
                for e in flags.get(k, []):
                    if e["script"]:
                        own.setdefault(c["formation"], set()).add(battle(PC.script_name(e["script"]), P.VANILLA))
        boxes = 0
        bad, unplaced, added, n = [], [], [], 0
        for k in sorted(changed):
            c = wc[k]
            if k not in vc:
                moved = (PC.MOVED_MAPS.get(k[0]), k[1], k[2])
                if moved not in flags:
                    added.append(k)                        # a chest WC adds that has no PR chest
                    continue
                k, vc_k = moved, vc.get(moved)             # WC's Thamasa: the chest it moved there
                if vc_k and (vc_k["type"], vc_k["contents"]) == (c["type"], c["contents"]):
                    continue
            if c["type"] == PC.ITEM:
                want = (M.item_content(c["contents"]), 1)
            elif c["type"] == PC.GOLD:
                want = (1, c["contents"] * 100)
            elif c["type"] == PC.MONSTER and vc.get(k, {}).get("type") == PC.MONSTER:
                want = None                                # a box: its battle (no PR box: unplaced below)
            else:
                continue                                   # empty, a new box (-chrm: the plugin's): not compared
            if want is None and k in flags:
                fights = {E.party_members(vmp[q]) for q in {M.party(c["formation"])} | own.get(c["formation"], set()) if q in vmp.rows}
                for e in flags[k]:
                    name = PC.script_name(e["script"]) if e["script"] else None
                    if not name:
                        bad.append(f"SNES box {vc[k]['id']}: PR chest {os.path.basename(os.path.dirname(e['file']))} #{e['obj']} has no battle")
                        continue
                    got, was = battle(name, os.path.join(self.seed, "files")), battle(name, P.VANILLA)
                    boxes += 1
                    if got is None or got not in mp.rows or E.party_members(mp[got]) not in fights:
                        bad.append(f"SNES box {vc[k]['id']}: {name} fights party {got}, not formation {c['formation']}")
                    elif mp[got]["battle_background_asset_id"] != vmp[was]["battle_background_asset_id"]:
                        bad.append(f"SNES box {vc[k]['id']}: {name} fights party {got} on background "
                                   f"{mp[got]['battle_background_asset_id']}, the box's {vmp[was]['battle_background_asset_id']}")
                continue
            if k not in flags and PC.MERGED.get(k) in flags:
                # a chest the PR dropped beside a pick-up it kept (the Phantom Train's rack): the
                # pick-up gives it after its own
                for e in flags[PC.MERGED[k]]:
                    n += 1
                    if want not in gifts(e)[1:]:
                        bad.append(f"SNES map {k[0]} ({k[1]},{k[2]}): SNES {want}, not among the pick-up's {gifts(e)}")
                continue
            if k not in flags:
                cid = vc[k]["id"]                          # WC's chest id (the vanilla table's order)
                if cid not in PC.WC_DUPLICATES and PC.wc_areas().get(cid):   # not a copy, reachable
                    unplaced.append((cid, PC.wc_areas()[cid]))
                continue
            for e in flags[k]:
                n += 1
                if chest(e) != want:
                    bad.append(f"SNES map {k[0]} ({k[1]},{k[2]}): SNES {want}, PR {chest(e)} ({os.path.basename(os.path.dirname(e['file']))} #{e['obj']})")
        self.assertGreater(n, 100)
        self.assertGreater(boxes, 5)                       # -cms shuffles the 11 boxes
        self.assertEqual(bad, [])
        # every changed SNES chest has a PR chest: the eight that start empty are the PR's scripted
        # empty chests (their scripts give what WC put in: South Figaro's basement, Phoenix Cave's
        # five, Albrook's two), the eleven boxes the PR's boxes (six had no pair, their PR flags
        # renumbered), the Phantom Train's two Tents the PR's pick-ups on its racks (the third
        # chest, dropped beside one, joins it: port_chests.MERGED)
        self.assertEqual([u for u in unplaced if u[0] not in NO_PR_CHEST], [],
                         "changed SNES chests (WC's chest id, area) with no PR chest")
        # WC's own new chests: Thamasa's, moved from the map before Leo's death (unreachable)
        # onto its WoB and WoR maps (340, 344: chests.py copy_thamasa_chests); the PR keeps them in
        # its one Thamasa, where they are compared above (port_chests.MOVED_MAPS)
        self.assertEqual(added, [], "WC's added chests with no PR chest")


if __name__ == "__main__":
    unittest.main()
