"""Offline tests for the Worlds Collide port (pr/tools/wc_events.py and the WC plugin's flag use).

The WC event scripts are built (wc_events.build, no seed needed) and run in a small model of
the game's script machine (the instructions the WC scripts use; Branch/Jump/Call continue after
their target, as the PR's interpreter does). Its flag storage is the PR's: scenario and treasure
flags are one 1024-flag array each in four 256-flag segments (DataStorage.kScenarioFlags /
kFlagSegment), so an index past 255 is a flag of the next segment. That is how the WC start's
"ScenarioFlag1 297" (Morph's constant) set "after the collapse" (ScenarioFlag2 41) and every WC
game began with Narshe in its WoR state (the user's playtest, 2026-11-06).

  pr/.venv/Scripts/python -m unittest discover -s pr/tests -p "test_wc.py" -v

The game itself is tested headless by pr/tools/run_tests.ps1 (WcTest.cs: a New Game, the wheel,
landing, walking into Narshe and out...).
"""
import collections
import glob
import json
import os
import re
import shutil
import sys
import tempfile
import unittest
import warnings

HERE = os.path.dirname(os.path.abspath(__file__))
PR = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(PR, "tools"))
import pr_script as S  # noqa: E402
import wc_events as E  # noqa: E402

SEGMENT = 256
SCENARIO = {"ScenarioFlag1": 0, "ScenarioFlag2": 1, "ScenarioFlag3": 2, "ScenarioFlag4": 3}
TREASURE = {"TreasureFlag1": 0, "TreasureFlag2": 1, "TreasureFlag3": 2, "TreasureFlag4": 3}
CMP = {"＝": lambda a, b: a == b, "≠": lambda a, b: a != b, "≧": lambda a, b: a >= b,
       "≦": lambda a, b: a <= b, "＞": lambda a, b: a > b, "＜": lambda a, b: a < b}

COLLAPSED = ("ScenarioFlag2", 41)     # "after the world's collapse": the merged towns' WoR state
GOT_FALCON = ("ScenarioFlag2", 97)
BLACKJACK_RELEASED = ("TreasureFlag4", 135)   # OwnedTransportationData's flag (SetVehicle sets it)
FALCON_RELEASED = ("TreasureFlag4", 136)
NARSHE_EXIT = "sc_e_0001_0"           # script 1180: Narshe's south edge (map 20010)
OPT_WHEEL, OPT_NEVER_MIND, OPT_FC = "E0412_00_119_c_01", "E0412_00_119_c_02", "E0559_00_119_c_01"


class State:
    """The game's DataStorage (flags, local variables, the last menu answer) and a party."""

    def __init__(self):
        self.scenario = [0] * (4 * SEGMENT)
        self.treasure = [0] * (4 * SEGMENT)
        self.vars = collections.defaultdict(int)
        self.selection = 0
        self.party = [1, 2, 3]

    def _slot(self, cat, i):
        if cat in SCENARIO:
            return self.scenario, SCENARIO[cat] * SEGMENT + i
        if cat in TREASURE:
            return self.treasure, TREASURE[cat] * SEGMENT + i
        return None, None

    def get(self, cat, i):
        if cat == "MessageSelection":
            return self.selection
        arr, k = self._slot(cat, i)
        return arr[k] if arr is not None else self.vars[(cat, i)]

    def set(self, cat, i, v):
        arr, k = self._slot(cat, i)
        if arr is not None:
            arr[k] = v
        else:
            self.vars[(cat, i)] = v

    def flag(self, f):
        return self.get(*f)


class Run:
    """One script run: the instructions it went through, the menus it offered, how it ended."""

    def __init__(self, sc, state, answers=(), syscalls=None, max_steps=20000, black=False, start=0):
        self.ms = sc["Mnemonics"]
        self.state = state
        self.answers = list(answers)
        self.syscalls = syscalls or {}
        self.trace, self.offered, self.stack = [], [], []
        self.end = None
        self.black = black                  # the screen faded out (FadeOut / FadeIn)
        self.black_lines = []               # lines that open while it is (nobody sees them)
        pc, steps = start, 0
        while pc < len(self.ms):
            steps += 1
            assert steps < max_steps, f"{sc['Name']} loops"
            m = self.ms[pc]
            mn, iv, sv = m["mnemonic"], m["operands"]["iValues"], m["operands"]["sValues"]
            self.trace.append((pc, mn, iv, sv))
            nxt = pc + 1
            if mn == "Branch":
                b = iv[1] if sv[2] == "imm" else state.get(sv[2], iv[1])
                if CMP[sv[1]](state.get(sv[0], iv[0]), b):
                    nxt = iv[2] + 1
            elif mn == "Jump":
                nxt = iv[0] + 1
            elif mn == "Call":
                self.stack.append(pc + 1)
                nxt = iv[0] + 1
            elif mn == "Return":
                if not self.stack:
                    self.end = ("Return",)
                    break
                nxt = self.stack.pop()
            elif mn == "Exit":
                self.end = (mn,)
                break
            elif mn == "ChangeScript":
                self.end = (mn, sv[0])
                break
            elif mn == "ChangeMap":
                self.end = ("ChangeMap", iv[0], iv[1], sv[0])
                break
            elif mn == "SetFlag":
                state.set(sv[0], iv[0], 1)
            elif mn == "ResetFlag":
                state.set(sv[0], iv[0], 0)
            elif mn == "Calc":
                v = iv[1] if sv[2] == "imm" else state.get(sv[2], iv[1])
                state.set(sv[0], iv[0], v if sv[1] == "←" else state.get(sv[0], iv[0]) + v)
            elif mn == "Select":
                keys = [k for k in sv if k]
                self.offered.append(keys)
                a = self.answers.pop(0) if self.answers else 0
                state.selection = keys.index(a) if isinstance(a, str) else a
            elif mn == "SysCall":
                f = self.syscalls.get(sv[0])
                if f:
                    f(state)
            elif mn in ("FadeOut", "FadeIn"):
                self.black = mn == "FadeOut"
            elif mn in ("Msg", "MsgFunfare") and self.black:
                self.black_lines.append(sv[0])
            elif mn == "BranchByLeader":
                if state.party and state.party[0] == iv[0]:
                    nxt = iv[1] + 1
            elif mn == "BranchByCharacter":
                present = iv[0] in state.party
                if (iv[1] == 0 and present) or (iv[1] == 1 and not present):
                    nxt = iv[3] + 1
            elif mn == "BranchByNumOfMember":
                if CMP[sv[0]](len(state.party), iv[1]):
                    nxt = iv[2] + 1
            pc = nxt

    def did(self, mnemonic, s0=None):
        return [t for t in self.trace if t[1] == mnemonic and (s0 is None or t[3][0] == s0)]


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def wc_start(state):
    """The plugin's WC:start with a seed active after the title's New Game: map-local WC_VAR = 1."""
    state.set("MapLocalVariable", E.WC_VAR, 1)


def gate(open_):
    """WC:gate <module>: ScenarioFlag4 SCRATCH = 1 when the gate is open (WcEvents.Gate)."""
    return lambda state: state.set(E.WC_FLAGS, E.SCRATCH, 1 if open_ else 0)


class WcScripts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        warnings.simplefilter("ignore", ResourceWarning)     # pr_script.load leaves its files to the GC
        cls.tmp = tempfile.mkdtemp(prefix="wc_scripts_")
        E.build(cls.tmp)
        cls.built = {}
        for p in glob.glob(os.path.join(cls.tmp, "**", "*.json"), recursive=True):
            cls.built[os.path.basename(p)[:-5].lower()] = p

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def script(self, name):
        return load(self.built.get(name.lower()) or S.path(name))

    def test_veldt_skips_tutorial_and_recruitment_cutscene(self):
        battle = self.script("sc_b_0239")
        segments = {s["Label"]: s["EntryPoint"] for s in battle["Segments"]}
        self.assertIn("wc_skip_tutorial", segments)
        self.assertIn("wc_skip_dried_meat_scene", segments)
        tutorial = battle["Mnemonics"][segments["wc_skip_tutorial"]:segments["wc_skip_tutorial"] + 5]
        dried = battle["Mnemonics"][segments["wc_skip_dried_meat_scene"]:segments["wc_skip_dried_meat_scene"] + 5]
        self.assertTrue(any(m["mnemonic"] == "Jump" and m["operands"]["iValues"][0] == 5 for m in tutorial))
        self.assertTrue(any(m["mnemonic"] == "Jump" and m["operands"]["iValues"][0] == 66 for m in dried))

        field = self.script("sc_e_0239_b")
        state = State()
        state.set(E.WC_FLAGS, E.WC_GAME, 1)
        rewarded = []
        run = Run(field, state, syscalls={
            "WC:gate veldt": gate(True),
            "WC:reward veldt 0": lambda st: rewarded.append(True),
        })
        self.assertEqual(rewarded, [True])
        self.assertEqual(state.get("ScenarioFlag1", 126), 1)
        self.assertFalse(any(t[1] == "Msg" and t[3][0].startswith("E0239_") for t in run.trace))

    def test_ultimas_open_without_their_lines(self):
        """Ultima Weapon (sc_b_0592) and Ultima Buster (sc_b_0977) open with lines WC removes from the
        SNES AI (the user, 2026-11-11: "ultima buster shouldn't have in battle introductory text"):
        a WC game runs neither's intro lines but keeps the pause's end; another game shows them all."""
        for name, prefix, count in (("sc_b_0592", "E0592_00_431_b_", 4), ("sc_b_0977", "E0977_00_554_b_", 7)):
            battle = self.script(name)
            # the intro: the battle start's Call (after WaitToBattleStart, the pause)
            call = next(m for m in battle["Mnemonics"][:8] if m["mnemonic"] == "Call")
            start = call["operands"]["iValues"][0]
            for wc in (1, 0):
                state = State()
                state.set(E.WC_FLAGS, E.WC_GAME, wc)
                run = Run(battle, state, start=start)
                shown = [t[3][0] for t in run.trace if t[1] == "Msg" and t[3] and t[3][0].startswith(prefix)]
                self.assertEqual(len(shown), 0 if wc else count, f"{name}, WC game {wc}: {shown}")
                self.assertTrue(any(t[1] == "BattlePause" for t in run.trace), f"{name}: the pause still ends")

    def test_veldt_reward_line_in_the_battle_window(self):
        """The reward given in the battle (the plugin, in the dried meat's two SysCalls) shows its
        line in the battle's own notice window (Msg [2, 0], as "Dispelled the curse on the
        shield!"), before the battle ends: not the plugin's overlay box (the user, 2026-11-11).
        Nothing shows when no reward was given (a Gau back from a Leap)."""
        import copy
        battle = self.script("sc_b_0239")
        events = {m["label"]: n for n, m in enumerate(battle["Mnemonics"]) if m["mnemonic"] == "Nop" and m.get("label")}
        for event, call in (("Event00", "バトル加入：ガウ"),
                            ("Event01", "ガウ初回予約")):
            for given in (True, False):
                sc = copy.deepcopy(battle)
                sc["Mnemonics"][0] = S.op("Jump", i=[events[event]])    # straight into that branch
                state = State()
                state.set(E.WC_FLAGS, E.WC_GAME, 1)
                run = Run(sc, state, syscalls={call: (lambda st: st.set(E.WC_FLAGS, E.REWARD_LINE, 1)) if given else (lambda st: None)})
                lines = [t for t in run.trace if t[1] == "Msg"]
                exits = [n for n, t in enumerate(run.trace) if t[1] == "BattleForceExitOnSave"]
                if given:
                    self.assertEqual([(t[2][:2], t[3][0]) for t in lines], [([2, 0], "WC_REWARD")], f"{event}: {lines}")
                    self.assertTrue(exits and run.trace.index(lines[0]) < exits[0], f"{event}: the line before the battle ends")
                else:
                    self.assertEqual(lines, [], f"{event} with no reward")
                self.assertEqual(state.get(E.WC_FLAGS, E.REWARD_LINE), 0, f"{event}: the flag cleared")
                self.assertTrue(exits, f"{event}: the battle still ends")

    def test_veldt_reward_leaves_the_party_where_it_fought(self):
        """The dried meat's first-join branch reserves sc_e_0239 on the world map; vanilla parks
        the Blackjack at (1, 1) and goes to Gau's scene map 574, which ends at the Crescent
        Mountain cave (the user's seed, 2026-11-11: two fades, vanilla Gau, then the cave). In a
        WC game the plugin gives the reward in the battle and turns the reservation away; if the
        script runs anyway: the reward on the spot (check open) or nothing (done), never the
        parking, the boarding or another map."""
        world = self.script("sc_e_0239")
        for done in (0, 1):
            state = State()
            state.set(E.WC_FLAGS, E.WC_GAME, 1)
            state.set(E.WC_FLAGS, E.CHECK_FLAGS["veldt"], done)
            rewarded = []
            run = Run(world, state, syscalls={"WC:reward veldt 0": lambda st: rewarded.append(True)})
            self.assertEqual(rewarded, [] if done else [True], f"done={done}")
            self.assertFalse(run.did("SetVehicle") or run.did("ChangeMap") or run.did("LoseVehicle"),
                             f"done={done}: {run.trace[-6:]}")
            self.assertFalse([t for t in run.did("SysCall") if not t[3][0].startswith("WC:")], f"done={done}")
            self.assertEqual(state.get(E.WC_FLAGS, E.CHECK_FLAGS["veldt"]), 1)
        # outside a WC game the vanilla scene still runs
        state = State()
        run = Run(world, state)
        self.assertEqual(run.end[:2], ("ChangeMap", 574))

    # --- the start -------------------------------------------------------------------------
    def new_game(self):
        st = State()
        r = Run(self.script("sc_e_0001"), st, syscalls={"WC:start": wc_start})
        st.set(*BLACKJACK_RELEASED, 1)          # SetVehicle releases the Blackjack
        return st, r

    def test_new_game_starts_on_the_blackjack_in_the_wob(self):
        st, r = self.new_game()
        self.assertEqual(r.end[:2], ("ChangeMap", E.DECK_MAP), r.end)
        park = [t for t in r.did("SetVehicle") if t[2][0] == E.BLACKJACK]
        self.assertTrue(park and park[0][2][1:4] == [E.WOB_MAP, *E.NARSHE_PARK], park)
        self.assertEqual(st.get(E.WC_FLAGS, E.WC_GAME), 1, "the WC game flag")
        for f in E.START_FLAGS:
            self.assertEqual(st.flag(f), 1, f"start flag {f}")
        self.assertEqual(st.flag(COLLAPSED), 0, "a WC game starts before the collapse (Narshe in its WoB state)")
        self.assertEqual(st.flag(GOT_FALCON), 0)

    def test_new_game_without_a_seed_is_vanilla(self):
        st = State()
        r = Run(self.script("sc_e_0001"), st)       # WC:start didn't run: no seed / not a New Game
        self.assertEqual(st.get(E.WC_FLAGS, E.WC_GAME), 0)
        self.assertNotEqual(r.end[:2], ("ChangeMap", E.DECK_MAP))

    # --- the wheel ---------------------------------------------------------------------------
    def wheel(self, st, answer, gate_open=False):
        return Run(self.script("sc_e_BJoutboard"), st, [answer],
                   {"WC:gate floating_continent": gate(gate_open)})

    def test_wheel_offers_the_wor(self):
        st, _ = self.new_game()
        r = self.wheel(st, OPT_NEVER_MIND)
        self.assertEqual(r.offered, [[OPT_WHEEL, "WC_HELM_WOR", OPT_NEVER_MIND]])
        self.assertEqual(r.end, ("Exit",))
        st2, _ = self.new_game()
        r2 = self.wheel(st2, OPT_NEVER_MIND, gate_open=True)
        self.assertEqual(r2.offered, [[OPT_FC, OPT_WHEEL, "WC_HELM_WOR", OPT_NEVER_MIND]], "the Floating Continent once its gate opens")

    def test_take_the_wheel_flies_in_the_wob(self):
        st, _ = self.new_game()
        r = self.wheel(st, OPT_WHEEL)
        self.assertEqual(r.end[:2], ("ChangeMap", E.WOB_MAP), r.end)
        self.assertEqual((st.flag(COLLAPSED), st.flag(GOT_FALCON)), (0, 0))
        self.assertFalse(r.did("SysCall", "WC:to_wor"))

    def test_fly_to_the_wor_and_back(self):
        st, _ = self.new_game()
        r = self.wheel(st, "WC_HELM_WOR")
        self.assertTrue(r.did("SysCall", "WC:to_wor"), "the plugin moves the Falcon here")
        self.assertEqual(r.end[:2], ("ChangeMap", E.WOR_MAP), r.end)
        self.assertEqual((st.flag(COLLAPSED), st.flag(GOT_FALCON)), (1, 1))
        self.assertEqual(self.narshe(st), (E.WOR_MAP, "ev_e_0609_1"))
        st.set(*FALCON_RELEASED, 1)
        back = Run(self.script("sc_e_FAoutboard"), st, ["WC_HELM_WOB"])
        self.assertTrue(back.did("SysCall", "WC:to_wob"))
        self.assertEqual(back.end[:2], ("ChangeMap", E.WOB_MAP), back.end)
        self.assertEqual((st.flag(COLLAPSED), st.flag(GOT_FALCON)), (0, 0))
        self.assertEqual(self.narshe(st), (E.WOB_MAP, "ev_e_0609"))

    # --- the Floating Continent trip ----------------------------------------------------------
    def deck(self, st):
        """The deck's map script: the entity sets it places."""
        return [t[3][0] for t in Run(self.script("sc_map_20170"), st).did("SetEntities")]

    def test_floating_continent_trip_from_the_wheel(self):
        """The user's Deck (2026-11-12): "flying to the floating continent just puts me back on
        the blackjack deck and i'm able to take the wheel again". The trip's deck must be the air
        force's (no wheel), WC's 3 fights, then Ultros/Chupon, Air Force and the landing; no line
        WC removes (party of three, the alert, "something strange", "Just ahead are Kefka")."""
        st, _ = self.new_game()
        st.party = [1, 2, 3, 4]                                   # WC: any party size
        lines = []
        r = self.wheel(st, OPT_FC, gate_open=True)
        lines += [t[3][0] for t in r.did("Msg")]
        self.assertEqual(r.end[:2], ("ChangeMap", E.DECK_MAP), r.end)
        self.assertEqual(r.end[3], "sc_e_BJoutboard", "the deck reloads with the party picked")
        r = Run(self.script("sc_e_BJoutboard"), st)
        lines += [t[3][0] for t in r.did("Msg")]
        self.assertEqual(r.end[:4], ("ChangeMap", 1, 110, "sc_e_0560_1"), "four members: on to the flight")
        r = Run(self.script("sc_e_0560_1"), st)
        self.assertEqual(r.end[:2], ("ChangeMap", E.DECK_MAP))
        r = Run(self.script(r.end[3]), st)                        # sc_e_0560_2: the trip begun
        self.assertEqual(r.end, ("ChangeScript", "sc_map_20170"))
        sets = self.deck(st)
        self.assertIn("ev_e_0560", sets, "the air force's deck")
        self.assertNotIn("ev_e_0421", sets, "no wheel while the air force attacks")
        # the fights (sc_e_1056 after the deck's 8 s timer, then sc_e_1058 on each timer)
        fights = 0
        r = Run(self.script("sc_e_1056"), st)
        lines += [t[3][0] for t in r.did("Msg")]
        fights += len(r.did("Encount"))
        nxt = Run(self.script("sc_e_1056_1"), st)
        while True:
            timers = [t[3][0] for t in nxt.did("SetTimer")]
            lines += [t[3][0] for t in nxt.did("Msg")]
            self.assertEqual(len(timers), 1, nxt.trace[-5:])
            self.assertNotIn("ev_e_0421", self.deck(st), "the deck after a fight: still no wheel")
            if timers[0] != "sc_e_1058":
                break
            r = Run(self.script("sc_e_1058"), st)
            fights += len(r.did("Encount"))
            nxt = Run(self.script("sc_e_1058_1"), st)
        self.assertEqual(fights, 3, "WC skips 3 of the 6 air force fights")
        self.assertEqual(timers[0], "sc_e_0565_trigger_set")
        self.assertIn("ev_e_0560_trigger", self.deck(st), "Ultros/Chupon's trigger")
        r = Run(self.script("sc_e_0565"), st)
        self.assertEqual([t[2][0] for t in r.did("EncountBoss")], [478])
        r = Run(self.script("sc_e_0565_1"), st)
        self.assertEqual([t[2][0] for t in r.did("EncountBoss")], [460])
        r = Run(self.script("sc_e_0565_2"), st)
        self.assertEqual(r.end[:2], ("ChangeMap", 431), "the landing")
        r = Run(self.script(r.end[3]), st)
        lines += [t[3][0] for t in r.did("Msg")]
        self.assertEqual(st.get("ScenarioFlag2", 30), 1)
        self.assertEqual(lines, [], "no line WC removes")
        sets = self.deck(st)
        self.assertIn("ev_e_0421", sets, "back on the deck later: the wheel")
        self.assertNotIn("ev_e_0560", sets)

    def test_floating_continent_trip_vanilla_lines(self):
        """Outside a WC game the wheel's trip keeps its lines and six fights' counter steps."""
        st = State()
        st.set("ScenarioFlag2", 28, 1)                            # the continent risen: vanilla's choice
        st.set(*BLACKJACK_RELEASED, 1)
        r = Run(self.script("sc_e_BJoutboard"), st, [OPT_FC])
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["E0560_00_119_a_01", "E0560_00_119_a_02"])
        r = Run(self.script("sc_e_1056_1"), st)
        self.assertEqual(st.get("MapLocalVariable", 1), 1)

    def test_floating_continent_trip_given_up(self):
        """A trip left before landing (the wheel was open on its deck before the fix; a save kept
        ScenarioFlag2 29): boarding again gives the plain deck with its wheel, which offers the
        trip again."""
        st, _ = self.new_game()
        st.set("ScenarioFlag2", 29, 1)
        self.assertNotIn("ev_e_0421", self.deck(st))
        r = Run(self.script("sc_e_BJinboard_1"), st)
        self.assertEqual(r.end, ("ChangeScript", "sc_map_20170"))
        self.assertEqual(st.get("ScenarioFlag2", 29), 0)
        self.assertIn("ev_e_0421", self.deck(st))
        # landed: the flag stays (the escape and the return to the airship board this way)
        st.set("ScenarioFlag2", 29, 1)
        st.set("ScenarioFlag2", 30, 1)
        Run(self.script("sc_e_BJinboard_1"), st)
        self.assertEqual(st.get("ScenarioFlag2", 29), 1)

    def test_dream_stooges_say_nothing(self):
        """The Dream Stooges' lines have no speaker in the PR (the plugin's rule kept them as
        narration; the user's Deck, 2026-11-12). WC removes "back off", "we're the 3 stooges" and
        "let's rumble": a WC game shows none, the two finds still set their flags and the third
        meeting still fights; outside WC all three scripts keep their lines."""
        for name, flag_, lines in (("sc_e_0896", 180, 1), ("sc_e_0898", 182, 1), ("sc_e_0901", None, 2)):
            for wc in (1, 0):
                st = State()
                st.set(E.WC_FLAGS, E.WC_GAME, wc)
                r = Run(self.script(name), st)
                self.assertEqual(len(r.did("Msg")), 0 if wc else lines, f"{name}, WC game {wc}")
                if flag_:
                    self.assertEqual(st.get("ScenarioFlag2", flag_), 1, f"{name}: the find's flag")
                    self.assertTrue(r.did("Flash"), f"{name}: the Stooge still vanishes")
                else:
                    self.assertEqual([t[2][0] for t in r.did("EncountBoss")], [461], f"{name}: the fight")

    def battle_script(self, name):
        rel = os.path.join("battle_script", "Assets", "GameAssets", "Serial", "Res", "Battle", "BattleScript", name + ".json")
        return load(os.path.join(self.tmp, rel)), load(os.path.join(S.P.VANILLA, rel))

    def test_ultros_battle_scenes(self):
        """Ultros' battle-script scenes (the user's Deck, 2026-11-12: "dialogue during an ultros
        battle": Ultros 1 shuffled into the Dream Stooges' place ran the Lete River script). In a
        WC game: the Lete River dive ends the fight with no lines and no raft talk; Esper
        Mountain's Relm never joins (no scene, the event marked done) and his defeat says nothing.
        Outside WC all of it plays."""
        built, _ = self.battle_script("sc_b_0065")
        event = next(n for n, m in enumerate(built["Mnemonics"]) if m["label"] == "Event00")
        for wc in (1, 0):
            st = State()
            st.set(E.WC_FLAGS, E.WC_GAME, wc)
            r = Run(built, st, start=event)
            lines = [t[3][0] for t in r.did("Msg")]
            self.assertEqual(len(lines), 0 if wc else 20, f"Lete River, WC game {wc}: {lines}")
            self.assertTrue(r.did("SysCall", "オルトロス潜る") and r.did("BattleForceExit"), f"Lete River, WC game {wc}: the dive, the end")
            effects = [t for t in r.did("PlayBattleEffect") if t[2][0] == 1]    # the party's animations
            self.assertEqual(bool(effects), not wc, f"Lete River, WC game {wc}: the raft talk's animations")
        built, _ = self.battle_script("sc_b_0547")
        labels = {m["label"]: n for n, m in enumerate(built["Mnemonics"]) if m["label"]}
        for wc in (1, 0):
            st = State()
            st.set(E.WC_FLAGS, E.WC_GAME, wc)
            r = Run(built, st, start=labels["Event00"])
            self.assertEqual(bool(r.did("SysCall", "バトル参戦：リルム")), not wc, f"Esper Mountain, WC game {wc}: Relm joins")
            self.assertEqual(len(r.did("Msg")), 0 if wc else 19, f"Esper Mountain, WC game {wc}: the scene's lines")
            self.assertEqual(st.get("ScriptLocalVariable", 2), 1, "the event marked done (on to the defeat's check)")
            r = Run(built, st, start=labels["Event_01"])
            self.assertEqual(len(r.did("Msg")), 0 if wc else 3, f"Esper Mountain's defeat, WC game {wc}")
            self.assertTrue(r.did("BattleForceExitOnSave"), "the fight still ends")

    def test_plugin_removed_battle_lines_are_ai_lines(self):
        """The plugin's WcRemovedBattleLines (WcEvents.cs) are lines of the PR AIs the seed keeps:
        Ultros 2/3, Vargas, Chadarnook (painting and demon); Chadarnook's "Back off!" isn't one."""
        src = open(os.path.join(PR, "plugin", "WcPlugin", "WcEvents.cs"), encoding="utf-8").read()
        block = src[src.index("WcRemovedBattleLines = new HashSet<string>"):]
        keys = set(re.findall(r'"(E\d{4}_\d\d_\d{3}_b_\d\d)"', block[:block.index("};")]))
        ai = set()
        for pat in ("sc_ai_12[DE]_Ultros.json", "sc_ai_103_Vargas.json", "sc_ai_11E_Chadarnook.json", "sc_ai_148_Chadarnook.json"):
            for f in glob.glob(os.path.join(S.P.VANILLA, "monster_ai", "**", pat), recursive=True):
                for m in load(f)["Mnemonics"]:
                    ai.update(s for s in m["operands"]["sValues"] if s)
        self.assertEqual(len(keys), 14)
        self.assertTrue(keys <= ai, f"not AI lines: {sorted(keys - ai)}")
        self.assertNotIn("E0773_01_446_b_01", keys, "WC keeps Chadarnook's 'Back off!'")

    def test_boss_battle_events(self):
        """The battle-script scenes WC blanks (wc_events boss_battle_events): in a WC game none of
        their lines, Sabin never joins Vargas' fight (its events only mark themselves done), the
        fights' own mechanics stay (Tunnel Armor's ATB reset, Vargas' fall after a Pummel, the UI
        back after each event); outside WC everything plays."""
        def run(name, label, wc):
            built, _ = self.battle_script(name)
            at = next(n for n, m in enumerate(built["Mnemonics"]) if m["label"] == label)
            st = State()
            st.set(E.WC_FLAGS, E.WC_GAME, wc)
            return Run(built, st, start=at), st
        cases = [   # script, event, lines outside WC, what must still run (op, sValue or None)
            ("sc_b_0055", "Event00", 16, ("Calc", None)),
            ("sc_b_0055", "Event01", 10, ("Calc", None)),
            ("sc_b_0055", "Event02", 2, ("BattleForceExitOnSave", None)),
            ("sc_b_0086", "Event00", 5, ("SysCall", "ATBリセット(ディッグアーマー)")),
            ("sc_b_0011", "Event00", 6, ("SysCall", "ShowBattleUI")),
            ("sc_b_0460_1", "Event00", 8, ("SysCall", "ShowBattleUI")),
            ("sc_b_0460_1", "Event02", 2, ("BattleForceExitOnSave", None)),
            ("sc_b_2022", "Event02", 3, ("SysCall", "LB:ケフカ降臨")),
        ]
        for name, label, lines, keep in cases:
            for wc in (1, 0):
                r, st = run(name, label, wc)
                shown = [t[3][0] for t in r.did("Msg")]
                self.assertEqual(len(shown), 0 if wc else lines, f"{name} {label}, WC game {wc}: {shown}")
                self.assertTrue(r.did(*keep), f"{name} {label}, WC game {wc}: {keep} still runs")
                if name == "sc_b_0055" and label == "Event00":
                    self.assertEqual(bool(r.did("SysCall", "バトル参戦：マッシュ")), not wc, f"Vargas, WC game {wc}: Sabin joins")
                    self.assertEqual(st.get("ScriptLocalVariable", 1), 1, "Sabin's event marked done")
                if name == "sc_b_0055" and label == "Event01":
                    self.assertEqual(st.get("ScriptLocalVariable", 1), 2, "the Blitz lesson marked done")
        # after Vargas, the field restores the battle party only Sabin's event saved
        for wc in (1, 0):
            st = State()
            st.set(E.WC_FLAGS, E.WC_GAME, wc)
            r = Run(self.script("sc_e_0055_1"), st, syscalls={"WC:gate mt_kolts": gate(True)})
            self.assertEqual(bool(r.did("SysCall", "バトルデータ復元：sc_b_0055")), not wc, f"Mt. Kolts' restore, WC game {wc}")

    def test_wc_removed_lines_are_cut(self):
        """Speakerless lines WC removes (E.WC_REMOVED_LINES; the user's Deck, 2026-11-12): in every
        copy of their script the line's place jumps to a block that, in a WC game, goes straight
        back past it, and outside WC runs it."""
        for name, key, why in E.WC_REMOVED_LINES:
            seen = 0
            for f in S.all_paths(name):
                rel = os.path.relpath(f, S.P.VANILLA)
                built = os.path.join(self.tmp, rel)
                self.assertTrue(os.path.exists(built), f"{name}: no WC copy ({why})")
                van, ms = load(f)["Mnemonics"], load(built)["Mnemonics"]
                for n, m in enumerate(van):
                    if m["mnemonic"] != "Msg" or m["operands"]["sValues"][0] != key:
                        continue
                    seen += 1
                    jump = ms[n]
                    self.assertEqual(jump["mnemonic"], "Jump", f"{name}[{n}] {key}")
                    t = jump["operands"]["iValues"][0]
                    gate_, back = ms[t + 1], ms[t + 2]
                    self.assertEqual((gate_["mnemonic"], gate_["operands"]["sValues"][0], gate_["operands"]["iValues"][:2]),
                                     ("Branch", E.WC_FLAGS, [E.WC_GAME, 0]), f"{name}[{n}]: the WC test")
                    self.assertEqual((back["mnemonic"], back["operands"]["iValues"][0]), ("Jump", n), f"{name}[{n}]: WC goes past it")
                    vanilla = ms[gate_["operands"]["iValues"][2] + 1]
                    self.assertEqual((vanilla["mnemonic"], vanilla["operands"]["sValues"][0]), ("Msg", key), f"{name}[{n}]: vanilla shows it")
            self.assertGreater(seen, 0, f"{name}: {key} not found")

    # --- Narshe ----------------------------------------------------------------------------
    def narshe(self, st):
        """Walk into Narshe (its map script) and out of its south edge: (the world map the exit
        leads to, the exit set the map placed)."""
        town = Run(self.script("sc_map_20010"), st)
        sets = [t[3][0] for t in town.did("SetEntities") if t[3][0].startswith("ev_e_0609")]
        self.assertEqual(len(sets), 1, sets)
        out = Run(self.script(NARSHE_EXIT), st)
        self.assertEqual(out.end[0], "ChangeMap")
        return out.end[1], sets[0]

    def test_narshe_after_landing_is_the_wobs(self):
        st, _ = self.new_game()
        self.wheel(st, OPT_WHEEL)
        self.assertEqual(self.narshe(st), (E.WOB_MAP, "ev_e_0609"), "WoB Narshe, and its exit leads back to the WoB")

    # --- the Zozo clock (-rc) -----------------------------------------------------------------
    def clock(self, st, answers, seed_answer):
        """The clock's three menus answered; WC:clock <hand> as the plugin answers it."""
        calls = {f"WC:clock {h}": (lambda s, h=h: s.set(E.WC_FLAGS, E.SCRATCH, 1 if s.selection == seed_answer[h] else 0))
                 for h in range(3)}
        return Run(self.script("sc_e_0299"), st, answers, calls)

    def test_zozo_clock_takes_the_seeds_time(self):
        st, _ = self.new_game()
        r = self.clock(st, [3, 0, 2], [3, 0, 2])          # 8:10:30, the seed's
        self.assertEqual(st.get(E.WC_FLAGS, E.QUEST_FLAG + E.QUEST_ZOZO_CLOCK), 1, "the quest: the clock set")
        self.assertTrue([t for t in r.did("Call") if t[2][0] == 53], "on to the reward (its Call 53)")

    def test_zozo_clock_refuses_vanillas_time(self):
        st, _ = self.new_game()
        r = self.clock(st, [2, 0, 4], [3, 0, 2])          # 6:10:50 (vanilla's) on a seed whose time is 8:10:30
        self.assertEqual(st.get(E.WC_FLAGS, E.QUEST_FLAG + E.QUEST_ZOZO_CLOCK), 0)
        self.assertTrue(r.did("Msg", "E0299_00_386_a_04"), "the clock keeps ticking")

    def test_zozo_clock_vanilla_outside_wc(self):
        st = State()                                       # not a WC game: the PR's own 6:10:50
        r = Run(self.script("sc_e_0299"), st, [2, 0, 4])
        self.assertTrue([t for t in r.did("Call") if t[2][0] == 53])

    # --- the opera: "Perform In Opera" ------------------------------------------------------------
    OPERA_END = "オペラシステム終了"     # the opera system's end call

    def stage(self, st):
        r = Run(self.script("sc_Map_20161_1"), st)
        return "ev_e_0318" in [t[3][0] for t in r.did("SetEntities")]

    def test_opera_stage_open_after_the_start(self):
        st, _ = self.new_game()
        self.assertTrue(self.stage(st), "the performance's trigger (ev_e_0318)")

    def test_opera_disruption_still_starts(self):
        st, _ = self.new_game()
        r = Run(self.script("sc_npc_20161_2_1"), st)
        self.assertEqual(r.end, ("ChangeScript", "sc_e_0330"), "the impresario: on to the disruption check")

    def test_opera_success_is_the_quest(self):
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0320_1"), st)
        self.assertEqual((st.get("ScenarioFlag1", 165), st.get(E.WC_FLAGS, E.QUEST_FLAG + E.QUEST_OPERA)), (1, 1))
        self.assertFalse(r.did("ChangePartySlot"), "the party stays the player's")
        self.assertFalse(self.stage(st), "performed once")

    def test_opera_failure_is_game_over(self):
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0318_1"), st, syscalls={self.OPERA_END: lambda s: s.set("MapLocalVariable", 7, 1)})
        self.assertEqual(r.end, ("ChangeMap", 584, 1, "sc_e_0328_1"), "WC: a failed performance is game over")
        self.assertFalse([t for t in r.did("SysCall") if "メンバー加入" in t[3][0]], "no free Locke/Celes")

    def test_no_opera_during_the_disruption_or_in_the_wor(self):
        st, _ = self.new_game()
        st.set("ScenarioFlag1", 167, 1)
        self.assertFalse(self.stage(st), "the disruption running")
        st.set("ScenarioFlag1", 177, 1)
        self.assertTrue(self.stage(st), "the disruption over")
        st.set(*COLLAPSED, 1)
        self.assertFalse(self.stage(st), "the WoR")

    # --- rooms reached out of story order (the user's playtest, 2026-11-06) --------------------
    def sets(self, name, st):
        return [t[3][0] for t in Run(self.script(name), st).did("SetEntities")]

    def test_narshe_shops_are_the_wobs(self):
        st, _ = self.new_game()
        for name in ("sc_map_20011_6", "sc_map_20011_8", "sc_map_20011_9", "sc_map_20011_10"):
            self.assertEqual(self.sets(name, st), ["ev_e_0259"], f"{name}: the WoB shop (randomized), not the opening's")

    def test_thamasa_shops_open(self):
        # the user's Deck game, 2026-11-12: the PR's shopkeepers wait for vanilla's landing near
        # Thamasa (ScenarioFlag2 12), which WC never plays; before it they're script-less stand-ins
        for wob in (True, False):
            st, _ = self.new_game()
            if not wob:
                st.set(*COLLAPSED, 1)
            for name in ("sc_map_20271_1", "sc_map_20271_2", "sc_map_20271_3"):
                self.assertEqual(self.sets(name, st)[-1:], ["ev_e_0518"], f"{name}: the shops (groups 48-51)")
                self.assertNotIn("ev_e_0001", self.sets(name, st))
        self.assertEqual(self.sets("sc_map_20271_2", State()), ["ev_e_0001"], "vanilla is unchanged")
        st, _ = self.new_game()
        self.assertEqual(st.get("ScenarioFlag2", 12), 0, "no Thamasa arrival scenes armed in town")
        self.assertNotIn("ev_e_0519", self.sets("sc_map_20270", st))

    def test_strago_not_home_in_thamasa(self):
        # WC clears Strago's at-home NPC bit; the PR's stand-in for him (ev_e_0001) has no script
        for wob in (True, False):
            st, _ = self.new_game()
            if not wob:
                st.set(*COLLAPSED, 1)
            r = Run(self.script("sc_map_20271_5"), st)
            self.assertEqual(r.did("SetEntities"), [], "nobody home")
            self.assertEqual(r.end, ("Exit",))
        self.assertEqual(self.sets("sc_map_20271_5", State()), ["ev_e_0001"], "vanilla is unchanged")

    def test_narshe_treasure_room_has_its_locked_chest(self):
        st, _ = self.new_game()
        self.assertEqual(self.sets("sc_map_20011_13", st), ["ev_e_0259"], "the locked chest and its owner")

    def test_south_figaro_unoccupied_with_celes_below(self):
        st, _ = self.new_game()
        self.assertEqual(st.get("ScenarioFlag1", 50), 0, "WC's South Figaro isn't occupied")
        self.assertIn("ev_e_0079", self.sets("sc_map_20071_12", st), "Celes chained in the basement")
        st.set("ScenarioFlag1", 59, 1)                     # freed: the check done
        self.assertNotIn("ev_e_0079", self.sets("sc_map_20071_12", st))

    def test_figaro_castle_guards_do_not_block_the_shop_wings(self):
        # WC event/figaro_castle_wob.py clears npc_bit.GUARDS_FIGARO_CASTLE at initialization.
        # In the PR these two soldiers are whole entity layers, selected before/after flag 15.
        st, _ = self.new_game()
        entities = self.sets("sc_map_20050", st)
        self.assertIn("ev_e_0018", entities, "the castle's ordinary NPCs remain")
        self.assertNotIn("ev_e_0018_1", entities, "the initial pair of blocking soldiers")
        self.assertNotIn("ev_e_0026", entities, "the later-positioned pair of blocking soldiers")
        self.assertIn("ev_e_0018_1", self.sets("sc_map_20050", State()), "vanilla is unchanged")

    def test_figaro_throne_reward_does_not_continue_the_story_scene(self):
        # SNES WC branches at CA/6623 to the reward routine and returns; the vanilla bytes after
        # it are free space. In particular it never reorganizes the party or changes maps into
        # the next Figaro/Kefka story scene.
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0029"), st, syscalls={"WC:gate figaro_castle_wob": gate(True)})
        self.assertEqual(len(r.did("SysCall", "WC:reward figaro_castle_wob 0")), 1)
        self.assertEqual(r.end, ("Exit",))
        self.assertEqual(r.did("OrganizePartySlot"), [])
        self.assertEqual(r.did("ChangePartySlot"), [])
        self.assertEqual(r.did("ChangeMap"), [], "no vanilla Figaro/Kefka continuation")
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["WC_REWARD"], "talking gives the reward directly")
        self.assertFalse(r.black, "the SNES reward routine fades the field back in before returning")
        self.assertEqual(r.black_lines, [], "the reward is visible")
        self.assertEqual(st.get(E.WC_FLAGS, E.CHECK_FLAGS["figaro_castle_wob"]), 1)
        self.assertEqual(st.get("ScenarioFlag1", 17), 1, "Edgar's throne actor is retired immediately")
        routine = self.script("sc_e_0029")["Mnemonics"]
        hidden = [m for m in routine if m["mnemonic"] == "SetPuppet" and m["operands"]["iValues"][1] == 18]
        self.assertTrue(any(routine[m["operands"]["iValues"][2] + 1]["mnemonic"] == "Hide" for m in hidden))

        # The same actor can still be clicked until the field is reloaded.  A completed check
        # must be inert immediately, not merely disappear on the next map load.
        again = Run(self.script("sc_e_0029"), st, syscalls={"WC:gate figaro_castle_wob": gate(True)})
        self.assertEqual(again.did("SysCall", "WC:reward figaro_castle_wob 0"), [])
        self.assertEqual(again.end, ("Exit",))

        hall = Run(self.script("sc_map_20050"), st)
        layers = [t[3][0] for t in hall.did("SetEntities")]
        self.assertNotIn("ev_e_0029", layers, "the throne story layer stays out of the main hall")
        self.assertNotIn("ev_e_0033", layers, "leaving the throne room does not start Kefka's scene")
        # vanilla plays Kefka's theme between naming Edgar (17) and Kefka's visit (20/21)
        self.assertEqual([t[2][0] for t in hall.did("PlayBGM")], [8], "the castle's theme after the check")
        vanilla = State()
        vanilla.set("ScenarioFlag1", 17, 1)
        self.assertEqual([t[2][0] for t in Run(self.script("sc_map_20050"), vanilla).did("PlayBGM")], [9],
                         "vanilla is unchanged")

    # --- music: WC's SNES game read in the SNES harness (seed gaadl68ix7j4), the user's
    # "what about the story changing other music" (2026-11-12) ---------------------------------
    def bgm(self, name, st, syscalls=None):
        return [t[2][0] for t in Run(self.script(name), st, syscalls=syscalls or {}).did("PlayBGM")]

    def test_map_music_follows_wcs_story_state(self):
        st, _ = self.new_game()
        for name in ("sc_map_20160", "sc_Map_20161_1", "sc_map_20161_2", "sc_map_20161_3", "sc_map_20161_4",
                     "sc_map_20161_5"):
            self.assertEqual(self.bgm(name, st), [32], f"{name}: the opera house's Spinach Rag, not the Overture")
        st.set("ScenarioFlag1", 165, 1)                  # the performance quest done
        self.assertEqual(self.bgm("sc_map_20160", st), [32], "no Wedding Waltz after the performance")
        st.set("ScenarioFlag1", 167, 1)                  # the disruption running: its own track
        self.assertEqual(self.bgm("sc_map_20160", st), [40])
        overture = State()
        overture.set("ScenarioFlag1", 158, 1)
        self.assertEqual(self.bgm("sc_map_20160", overture), [35], "vanilla keeps the Overture")

        st, _ = self.new_game()
        self.assertEqual(self.bgm("sc_map_20011_14", st), [3], "Narshe's theme where Terra woke, not Awakening")
        self.assertEqual(self.bgm("sc_map_20011_14", State()), [4], "vanilla: Awakening before Terra's escape")
        self.assertEqual(self.bgm("sc_map_20071_5", st), [21], "South Figaro's pub: no Shadow's theme without Shadow")
        self.assertEqual(self.bgm("sc_map_20071_12", st), [21], "the basement: the town's theme, not the occupation's")
        shadow = State()
        shadow.set("ScenarioFlag1", 27, 1)
        self.assertEqual(self.bgm("sc_map_20071_5", shadow), [12], "vanilla: Shadow's theme while he's there")
        self.assertEqual(self.bgm("sc_map_20131_1", st, {"WC:gate kohlingen": gate(True)}), [21],
                         "Kohlingen's inn keeps the town's theme with the check open")
        st.set("ScenarioFlag2", 97, 1)                   # in Mobliz's WoR story
        self.assertEqual(self.bgm("sc_map_20341_9", st), [57], "Mobliz WoR: From That Day On..., as WC's")
        phunbaba = State()
        phunbaba.set("ScenarioFlag2", 97, 1)
        phunbaba.set("ScenarioFlag2", 99, 1)
        self.assertEqual(self.bgm("sc_map_20341_9", phunbaba), [15], "vanilla: The Unforgiven between the fights")

        for open_ in (False, True):
            st, _ = self.new_game()
            self.assertEqual(self.bgm("sc_map_30410", st, {"WC:gate imperial_camp": gate(open_)}), [13],
                             f"the Imperial Camp's own music on arrival (gate open: {open_})")
        self.assertEqual(self.bgm("sc_e_0114", st), [15], "The Unforgiven as the party joins Cyan's duels")

    @staticmethod
    def theme_calls(script, char):
        """WC:theme as the plugin answers it for a seed whose reward there is character `char`
        (None: an item or esper)."""
        calls = {}
        for m in script["Mnemonics"]:
            s = (m["operands"].get("sValues") or [""])[0]
            if m["mnemonic"] == "SysCall" and s.startswith("WC:theme"):
                hit = char in [int(x) for x in s.split()[3].split(",")]
                calls[s] = (lambda h: lambda st: st.set(E.WC_FLAGS, E.SCRATCH, 1 if h else 0))(hit)
        return calls

    def test_check_scenes_play_the_reward_characters_theme(self):
        # WC (music/song_utils.py, the events' character_music_mod): the vanilla character's theme
        # unless the reward is a character, then that character's
        sites = [("sc_map_20090", "gau_father_house", 12), ("sc_e_0218_2", "baren_falls", 19),
                 ("sc_e_0438", "lone_wolf", 48), ("sc_e_0735", "mt_zozo", 14), ("sc_e_0775", "owzer_mansion", 50),
                 ("sc_e_0776", "fanatics_tower", 50), ("sc_e_0344_2", "opera_house_wob", 42)]
        for name, module, vanilla in sites:
            sc = self.script(name)
            for char in (None, 1, 4, 6, 11, 14):
                st, _ = self.new_game()
                calls = dict(self.theme_calls(sc, char), **{f"WC:gate {module}": gate(True)})
                got = [t[2][0] for t in Run(sc, st, syscalls=calls).did("PlayBGM")]
                want = vanilla if char is None else E.CHARACTER_THEMES[char]
                self.assertTrue(got and got[0] == want, f"{name}, reward character {char}: {got}, want {want} first")

    def test_check_scenes_music_as_wcs(self):
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0672_1"), st, syscalls={"WC:gate figaro_castle_wor": gate(True)})
        order = [t[1] if t[1] == "PlayBGM" else t[3][0] for t in r.trace
                 if t[1] == "PlayBGM" and t[2][0] == 8 or t[1] == "SysCall" and t[3][0].startswith("WC:reward")]
        self.assertEqual(order[:2], ["PlayBGM", "WC:reward figaro_castle_wor 0"],
                         "Edgar & Sabin's theme after the Tentacles, before the reward (WC: StartSong 0x0a)")
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0709_2"), st, syscalls={"WC:gate mobliz_wor": gate(True)})
        self.assertEqual([t[2][0] for t in r.did("PlayBGM")], [15], "The Unforgiven before Phunbaba (WC: 0x32)")

    def test_figaro_castle_shops_use_the_randomized_wob_copies(self):
        for name in ("sc_npc_20051_11_1", "sc_npc_20051_12_1"):
            for party in ([1], [5], [6]):       # ordinary, Edgar discount, Sabin discount
                st, _ = self.new_game()
                st.party = list(party)
                r = Run(self.script(name), st)
                shops = [t[3][0] for t in r.did("SysCall") if t[3][0].startswith("ショップを開く")]
                self.assertEqual(len(shops), 1, (name, party, shops))
                self.assertIn("城移動可能後", shops[0], "the post-Kefka WoB product group is seed-randomized")
                self.assertNotIn("初期", shops[0])
            vanilla = Run(self.script(name), State())
            shops = [t[3][0] for t in vanilla.did("SysCall") if t[3][0].startswith("ショップを開く")]
            self.assertEqual(len(shops), 1)
            self.assertIn("初期", shops[0], "non-WC games keep the vanilla opening inventory")

    def test_check_scenes_show_no_story_text(self):
        """Every scene that gates or rewards a check, and the scenes it moves on to (ChangeMap /
        ChangeScript), run in a WC game with the gate open: no story line the plugin would let
        through in play (WcEvents.SkipStoryLine: a character's line, or any line in a reward's
        scene, is skipped; narration elsewhere, a menu's question, a line saying what the party
        got are shown). WC cuts its checks' dialogue; only the auction's and Tzen's sale talk
        stays (the user, 2026-11-11, asked which events still show text: Ebot's Rock's
        "That evening..." did)."""
        msg = os.path.join(PR, "vanilla", "message", "Assets", "GameAssets", "Serial", "Data", "Message")

        def table(name):
            out = {}
            for line in open(os.path.join(msg, name), encoding="utf-8"):
                k, _, v = line.rstrip("\n").partition("\t")
                out[k] = v
            return out

        text, speaker = table("story_mes_en.txt"), table("story_cha_en.txt")
        for k, v in E.MESSAGES.items():
            text[k], speaker[k] = v, ""
        keep = re.compile(r"^<CT>(Obtained|Received|Learned|Found|Got|HP and MP)|dragons? remaining|magicite!|joined the party", re.I)

        def calls(sc):
            return [(m["operands"].get("sValues") or [""])[0] for m in sc["Mnemonics"] if m["mnemonic"] == "SysCall"]

        def shown(sc, name, pc, key):
            if not key.startswith("E") or not name.lower().startswith("sc_e_"):
                return False                                   # the plugin's own text, maps and NPCs: not this test's
            ms = sc["Mnemonics"]
            for k in range(pc + 1, min(len(ms), pc + 5)):
                if ms[k]["mnemonic"] in ("Nop", "MsgClose", "Wait"):
                    continue
                if ms[k]["mnemonic"].startswith("Select"):
                    return False                               # a menu's question
                break
            c = calls(sc)
            reward_scene = any(x.startswith("WC:reward") for x in c) and not any(x.startswith(("WC:announce", "WC:offer")) for x in c)
            if not speaker.get(key, "").strip() and not reward_scene:
                return True
            return bool(keep.search(text.get(key, "")))

        class Calls(dict):
            def get(self, k, d=None):
                return gate(True) if k.startswith("WC:gate") else (lambda st: None)

        modules = collections.defaultdict(list)
        for name in self.built:
            for s in calls(self.script(name)):
                m = re.match(r"WC:(gate|reward) (\w+)", s)
                if m:
                    modules[m.group(2)].append(name)
        found = []
        for mod, names in sorted(modules.items()):
            if mod in ("auction_house", "tzen"):
                continue                                       # the sale's talk is the sale
            seen = set()
            for start in sorted(set(names)):
                todo = [start]
                while todo and len(seen) < 200:
                    name = todo.pop(0)
                    if name.lower() in seen:
                        continue
                    seen.add(name.lower())
                    try:
                        sc = self.script(name)
                    except Exception:
                        continue
                    st = State()
                    st.set(E.WC_FLAGS, E.WC_GAME, 1)
                    if mod in E.CHECK_FLAGS:
                        st.set(E.WC_FLAGS, E.CHECK_FLAGS[mod], 0)
                    run = Run(sc, st, syscalls=Calls(), max_steps=5000)
                    for pc, mn, iv, sv in run.trace:
                        if mn in ("Msg", "MsgFunfare") and sv and shown(sc, name, pc, sv[0]):
                            found.append(f"{mod}: {name} {sv[0]} '{text.get(sv[0], '')[:60]}'")
                    if run.end and run.end[0] == "ChangeMap" and run.end[3]:
                        todo.append(run.end[3])
                    elif run.end and run.end[0] == "ChangeScript":
                        todo.append(run.end[1])
        self.assertEqual(found, [], "story lines a WC game would show in its checks' scenes")

    def test_doma_siege_starts_as_vanilla_enters_it(self):
        # The siege scene (sc_e_0099_3) runs Doma's map script itself (ChangeScript [1]) before it
        # fades in, so the map script can't start it with ChangeScript: that left the scene running
        # for good, the screen frozen (the user's Deck, 2026-11-11; WcTest doma_start). It is
        # entered as vanilla's sc_e_0099_2 enters it: out of the screen (instant: the entrance
        # doesn't flash), then map 75 point 2 with the scene as the arrival script.
        st, _ = self.new_game()
        r = Run(self.script("sc_map_20100"), st, syscalls={"WC:gate doma_wob": gate(True)})
        self.assertEqual(r.end, ("ChangeMap", 75, 2, "sc_e_0099_3"))
        self.assertTrue(r.black, "the screen is out before the map change")
        self.assertEqual(r.did("ChangeScript"), [], "never started from inside the map script")
        self.assertEqual(st.get("ScenarioFlag1", 72), 1, "the siege state is established")
        # arriving with the siege on: the map script carries on as vanilla (no second start)
        r2 = Run(self.script("sc_map_20100"), st, syscalls={"WC:gate doma_wob": gate(True)})
        self.assertNotEqual(r2.end[:2], ("ChangeMap", 75))

    def test_daryl_tomb_reward_does_not_acquire_the_falcon(self):
        # SNES WC returns after opening the tomb staircase; the staircase itself is the ordinary
        # way back to the WoR.  The PR must not continue into sc_e_0703_1's Falcon movie.
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0703"), st, syscalls={"WC:gate daryl_tomb": gate(True)})
        self.assertEqual(len(r.did("SysCall", "WC:reward daryl_tomb 0")), 1)
        self.assertEqual(r.end, ("Exit",))
        self.assertEqual(r.did("ChangeMap"), [], "no Falcon-acquisition scene")
        self.assertFalse(r.black, "the field is visible when control returns in the tomb")
        self.assertEqual(r.black_lines, [])

    def test_queen_room_cues_stone_only_on_the_queens_art(self):
        # The queen's room (sc_map_30811_7) cues its own 'stone' (one frame, Stone_00 of the
        # queen's sheet) on the statue. A seed whose reward here is a character shows the statue
        # as that character (wc_npcs), whose sheet lacks the frame: the room stayed black (the
        # user's Deck, 2026-11-12, Gogo). A WC game cues it only while the statue is the queen's art.
        sc = self.script("sc_map_30811_7")
        anims = {a["name"]: [f["sprite_name"] for f in a["frames"]] for a in sc["Animations"]}

        def statue_cues(r):
            out = []
            for t in r.did("SetPuppet"):
                if t[2][1] != E.QUEEN:
                    continue
                at = t[2][2] + 1                       # the routine runs after its labelled Nop
                while sc["Mnemonics"][at]["mnemonic"] not in ("Exit", "Return"):
                    if sc["Mnemonics"][at]["mnemonic"] == "CueAnim":
                        out.append(sc["Mnemonics"][at]["operands"]["sValues"][0])
                    at += 1
            return out

        def shows_queen(yes):
            return lambda state: state.set(E.WC_FLAGS, E.SCRATCH, 1 if yes else 0)

        call = f"WC:is_asset {E.QUEEN} {E.QUEEN_ART}"
        st, _ = self.new_game()
        st.set("ScenarioFlag2", 172, 1)
        other = Run(sc, st, syscalls={call: shows_queen(False)})
        self.assertEqual(other.did("SysCall", call)[0][3][0], call)
        self.assertEqual(statue_cues(other), ["wc_stone"])
        self.assertEqual(other.end, ("Exit",))
        queen = Run(sc, st, syscalls={call: shows_queen(True)})
        self.assertEqual(statue_cues(queen), ["stone"])
        vanilla = Run(sc, State())
        self.assertEqual(statue_cues(vanilla), ["stone"])
        self.assertEqual(vanilla.did("SysCall"), [], "outside a WC game: vanilla")
        # the grey pose is the sheet's own first frame: on every sheet a re-skin can show
        self.assertEqual(anims["stone"], ["Stone_00"])
        self.assertEqual(anims["wc_stone"], ["Default_00"])
        import wc_npc_anim_audit as A
        import wc_npcs
        mo = A.mapobjects()
        for art in sorted(set(wc_npcs.CHARACTER_NPC.values()) | set(wc_npcs.WC_SPRITE_NPC.values()) | {E.QUEEN_ART}):
            frames = {x for v in A.sheet(mo[art]).values() for x in v}
            self.assertIn("Default_00", frames, mo[art])

    def test_esper_mountain_ends_at_the_reward(self):
        # SNES WC replaces the entire post-Ultros scene, not only Relm's join at its end.
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0547_1"), st)
        self.assertEqual(len(r.did("SysCall", "WC:reward esper_mountain 0")), 1)
        self.assertEqual(r.end, ("Exit",))
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["WC_REWARD"], "no Relm/Thamasa continuation")
        self.assertEqual(r.did("Call"), [], "no post-Ultros performance")
        self.assertEqual(st.get("ScenarioFlag2", 24), 1, "Ultros defeated")
        self.assertFalse(r.black)

    def test_mt_kolts_ends_at_the_reward(self):
        # The WC item/esper path returns immediately after Vargas.  A common compact path also
        # prevents the vanilla Sabin-family scene from addressing an arbitrary seed reward.
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0055_1"), st)
        # the restore pairs with the save in the fight's Sabin event, which a WC game no longer
        # plays (boss_battle_events, 2026-11-12): nothing to restore
        self.assertFalse(r.did("SysCall", "バトルデータ復元：sc_b_0055"), "no battle data restored")
        self.assertEqual(len(r.did("SysCall", "WC:reward mt_kolts 0")), 1)
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["WC_REWARD"], "no Sabin reunion dialogue")
        self.assertEqual(r.did("Call"), [], "no Sabin reunion animation")
        self.assertEqual(st.get("ScenarioFlag1", 41), 1, "Vargas defeated")
        self.assertEqual(r.end, ("Exit",))
        self.assertFalse(r.black)

    def test_fanatics_tower_skips_the_relm_strago_scene(self):
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0776"), st, syscalls={"WC:gate fanatics_tower": gate(True)})
        self.assertEqual(len(r.did("SysCall", "WC:reward fanatics_tower 0")), 1)
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["WC_REWARD"], "no Relm/Strago story dialogue")
        self.assertEqual(st.get("ScenarioFlag2", 136), 1, "tower recruitment check done")
        self.assertTrue(r.did("RecoveryHP"), "the vanilla cleanup/recovery tail remains")
        self.assertEqual(r.end, ("Exit",))

    def test_collapsing_house_has_only_its_reward_line(self):
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0639"), st)
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["WC_REWARD"],
                         "WC removes every post-escape story line")
        self.assertEqual(len(r.did("SysCall", "WC:reward collapsing_house 0")), 1)
        self.assertEqual(st.get("ScenarioFlag2", 56), 1)
        self.assertEqual(r.end, ("Exit",))
        self.assertFalse(r.black)

    def test_serpent_trench_helmet_has_no_story_cutscene(self):
        # WC event/serpent_trench.py removes the party split, Gau/Sabin/Cyan dialogue and the
        # automatic transition. Finding the helmet leaves the party in Crescent Mountain.
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0246"), st, syscalls={"WC:gate serpent_trench": gate(True)})
        self.assertEqual(r.did("Msg"), [], "no Gau/Sabin/Cyan story dialogue")
        self.assertEqual(r.did("ChangeMap"), [], "finding the helmet does not advance the story")
        self.assertEqual(r.did("SetPuppet") and [t[2][1] for t in r.did("SetPuppet")], [9, 10, 14, 15, 16])
        self.assertEqual(st.get("ScenarioFlag1", 133), 1, "the diving helmet is found")
        self.assertEqual(r.end, ("Exit",))

        closed, _ = self.new_game()
        r2 = Run(self.script("sc_e_0246"), closed, syscalls={"WC:gate serpent_trench": gate(False)})
        self.assertEqual((closed.get("ScenarioFlag1", 133), r2.did("Msg"), r2.did("ChangeMap")), (0, [], []))

    def test_serpent_trench_arrival_keeps_blackjack_boardable(self):
        # ChangeScript continues in the game but is terminal in this test VM, so inspect the
        # injected arrival block itself.
        ms = self.script("sc_e_1502_3")["Mnemonics"]
        ship = next(i for i, m in enumerate(ms) if m["mnemonic"] == "SetVehicle" and m["operands"]["iValues"][0] == E.BLACKJACK)
        self.assertEqual(ms[ship]["operands"]["iValues"][1:6], [E.WOB_MAP, 116, 61, 1, 3],
                         "park adjacent to Nikeah's 116,61 exit on PR's explicit world-object layer")
        released = ms[ship + 1]
        self.assertEqual((released["mnemonic"], released["operands"]["sValues"][0], released["operands"]["iValues"][0]),
                         ("SetFlag", "TreasureFlag4", 135), "the visible Blackjack still has its boarding prompt")

    def test_serpent_trench_jump_appears_after_finding_helmet(self):
        st, _ = self.new_game()
        before = Run(self.script("sc_map_30490"), st)
        self.assertEqual(before.did("SetEntities", "ev_e_0270"), [], "no trench jump before the helmet")
        st.set("ScenarioFlag1", 133, 1)
        after = Run(self.script("sc_map_30490"), st)
        self.assertEqual(len(after.did("SetEntities", "ev_e_0270")), 1, "the player-controlled Jump in? trigger appears")

        # Outside WC, the PR's original story-progress condition remains authoritative.
        vanilla = State()
        vanilla.set("ScenarioFlag1", 138, 1)
        self.assertEqual(len(Run(self.script("sc_map_30490"), vanilla).did("SetEntities", "ev_e_0270")), 1)

    def test_cutscene_changes_leave_vanilla_games_alone(self):
        # Every cut is guarded by WC_GAME; representative vanilla story lines and transitions
        # remain when the same scripts run outside a seed.
        self.assertTrue(Run(self.script("sc_e_0547_1"), State()).did("Msg", "E0547_02_425_a_01"))
        self.assertTrue(Run(self.script("sc_e_0055_1"), State()).did("Msg", "E0055_05_343_a_01"))
        self.assertTrue(Run(self.script("sc_e_0776"), State()).did("Msg", "E0776_00_476_a_01"))
        self.assertTrue(Run(self.script("sc_e_0639"), State()).did("Msg", "E0639_00_201_a_01"))
        self.assertTrue(Run(self.script("sc_e_0246"), State()).did("Msg", "E0246_00_378_a_01"))
        self.assertEqual(Run(self.script("sc_e_0703"), State()).end[0], "ChangeMap")

    def test_story_scenes_outside_checks_are_not_placed(self):
        # The user's playtest, 2026-11-12: Setzer and Cid's engine-room scene played (silently:
        # the plugin skips its lines). Story scenes outside WC's checks never start in a WC game;
        # the same map scripts outside a WC game still place them.
        for name, _, layer, why in E.NO_SCENE_LAYERS:
            st, _ = self.new_game()
            self.assertEqual(Run(self.script(name), st).did("SetEntities", layer), [], why)
            st.set(E.WC_FLAGS, E.WC_GAME, 0)
            self.assertEqual(len(Run(self.script(name), st).did("SetEntities", layer)), 1, f"vanilla: {why}")

    def test_narshe_wor_has_no_thief_scene(self):
        st, _ = self.new_game()
        st.set(*COLLAPSED, 1)
        st.set(*GOT_FALCON, 1)
        r = Run(self.script("sc_Map_20010"), st)
        self.assertTrue(r.did("SetEntities", "ev_e_0703"), "Narshe's WoR state")
        self.assertEqual(r.did("SetEntities", "ev_e_0777"), [], "no thief walking up at the gate")

    def test_blackjack_engine_room_stairs_have_no_scene(self):
        st, _ = self.new_game()
        self.assertEqual(st.get("ScenarioFlag2", 1), 1, "WC's start: Gestahl's dinner over (what starts the scene)")
        self.assertEqual(Run(self.script("sc_e_1033"), st).end, ("ChangeMap", 122, 1, ""))
        st.set(E.WC_FLAGS, E.WC_GAME, 0)
        self.assertEqual(Run(self.script("sc_e_1033"), st).end, ("ChangeMap", 122, 1, "sc_e_1033_1"), "vanilla keeps it")

    def test_sabins_cabin_door_has_no_scene(self):
        st, _ = self.new_game()
        self.assertEqual(Run(self.script("sc_e_0046_0"), st).end, ("ChangeMap", 37, 1, ""))

    def test_no_shadow_in_south_figaros_pub_and_no_rachel_flashback(self):
        st, _ = self.new_game()
        self.assertEqual(st.get("ScenarioFlag1", 27), 1, "WC's start: what places Shadow in the pub")
        r = Run(self.script("sc_map_20071_4"), st)
        self.assertEqual(r.did("SetEntities", "ev_e_0041"), [], "WC hides Shadow (no intro, no naming screen)")
        self.assertEqual([t[2][0] for t in r.did("PlayBGM")], [21], "the pub's own music")
        self.assertEqual(Run(self.script("sc_e_0283"), st).end, ("ChangeScript", "sc_e_0288"), "the herbalist's line only")
        st.set(E.WC_FLAGS, E.WC_GAME, 0)
        self.assertEqual(len(Run(self.script("sc_map_20071_4"), st).did("SetEntities", "ev_e_0041")), 1, "vanilla keeps him")
        self.assertEqual(Run(self.script("sc_e_0283"), st).end, ("ChangeScript", "sc_e_0283_1"))

    def test_kefkas_jail_and_kohlingens_old_man_do_nothing(self):
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0498"), st)
        self.assertEqual((r.did("Msg"), r.did("SysCall"), r.end), ([], [], ("Exit",)), "no monologue, no dinner timer")
        r = Run(self.script("sc_e_0281"), st)
        self.assertEqual((r.did("Msg"), r.did("ChangeScript"), r.end), ([], [], ("Exit",)))
        self.assertEqual(st.get("MapLocalVariable", 1), 1, "once a visit, as vanilla")

    def test_daryl_tomb_stairs_give_the_reward_and_lead_out(self):
        # WC event/daryl_tomb.py: the reward after Dullahan, the stairs back to the World of Ruin;
        # no flashbacks, no Falcon hangar (map 577 has no way out)
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0700"), st, syscalls={"WC:gate daryl_tomb": gate(True)})
        self.assertEqual(len(r.did("SysCall", "WC:reward daryl_tomb 0")), 1)
        self.assertEqual(r.did("SysCall", "控えに移動：セッツァー"), [],
                         "Setzer stays in the party")
        self.assertEqual(r.end, ("ChangeMap", *E.DARILL_EXIT, ""))
        self.assertEqual(st.get(E.WC_FLAGS, E.CHECK_FLAGS["daryl_tomb"]), 1)
        self.assertFalse(r.black)
        again = Run(self.script("sc_e_0700"), st, syscalls={"WC:gate daryl_tomb": gate(True)})
        self.assertEqual((again.did("SysCall", "WC:reward daryl_tomb 0"), again.end), ([], ("ChangeMap", *E.DARILL_EXIT, "")))
        closed, _ = self.new_game()
        r = Run(self.script("sc_e_0700"), closed, syscalls={"WC:gate daryl_tomb": gate(False)})
        self.assertEqual((r.did("SysCall", "WC:reward daryl_tomb 0"), r.end), ([], ("ChangeMap", *E.DARILL_EXIT, "")))
        self.assertEqual(closed.get(E.WC_FLAGS, E.CHECK_FLAGS["daryl_tomb"]), 0, "comes back later")

    def test_later_rewards_stay_open_after_the_first(self):
        # gate_at keyed every scene of a module to its first reward's flag: once Ifrit/Shiva were
        # taken, Number 024 and the tubes ended at once (no 2nd/3rd reward, no elevator down);
        # once Mog's scene was done, the Gold Hairpin's place too
        st, _ = self.new_game()
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["magitek_factory"], 1)
        r = Run(self.script("sc_e_0401"), st, syscalls={"WC:gate magitek_factory": gate(True)})
        self.assertTrue(r.did("EncountBoss") or r.did("Encount"), "Number 024 fights after the first reward")
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["magitek_factory_2"], 1)
        r = Run(self.script("sc_e_0401"), st, syscalls={"WC:gate magitek_factory": gate(True)})
        self.assertEqual((r.did("EncountBoss"), r.did("Encount"), r.end), ([], [], ("Exit",)), "not twice")
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["lone_wolf"], 1)
        r = Run(self.script("sc_e_0439"), st, syscalls={"WC:gate lone_wolf": gate(True)})
        self.assertEqual(len(r.did("SysCall", "WC:reward lone_wolf 1")), 1, "the hairpin's reward after Mog's")

    def test_magitek_factory_end_is_wc_s(self):
        # WC event/magitek_factory.py: no tube espers'/Celes scene (Celes stays), no elevator ride
        # with Cid, no Cid scene; the mine cart, the Cranes, then the airship outside Vector
        st, _ = self.new_game()
        st.party = [7, 1]                                   # Celes in the party
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["magitek_factory"], 1)
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["magitek_factory_2"], 1)
        r = Run(self.script("sc_e_0402"), st, syscalls={"WC:gate magitek_factory": gate(True)})
        self.assertEqual(len(r.did("SysCall", "WC:reward magitek_factory 2")), 1)
        self.assertEqual(r.did("SysCall", "メンバー離脱：セリス"), [], "Celes doesn't leave")
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["WC_REWARD"], "no scene")
        self.assertEqual(r.did("GetItem"), [], "no tube espers")
        self.assertEqual((st.get("ScenarioFlag1", 192), len(r.did("SetEntities", "ev_e_0403"))), (1, 1), "the elevator")
        self.assertEqual(st.get(E.WC_FLAGS, E.CHECK_FLAGS["magitek_factory_3"]), 1)
        again = Run(self.script("sc_e_0402"), st, syscalls={"WC:gate magitek_factory": gate(True)})
        self.assertEqual((again.did("SysCall"), again.end), ([], ("Exit",)), "done: nothing")

        r = Run(self.script("sc_e_0403"), st)
        self.assertEqual((r.end, st.get("ScenarioFlag1", 193)), (("ChangeMap", 404, 1, ""), 1), "straight down to Cid")
        r = Run(self.script("sc_e_0404"), st)
        self.assertEqual((r.did("Msg"), r.end), ([], ("ChangeMap", 578, 1, "sc_e_0404_1")), "talking to Cid: the cart")
        self.assertEqual(st.get("ScenarioFlag1", 194), 0, "no escape state (it brings Setzer's scene back to Vector)")

        r = Run(self.script("sc_e_0404_8"), st)
        self.assertEqual(r.did("SetEntities", "ev_e_0405"), [], "no Setzer in Vector")
        boss = r.did("EncountBoss")
        self.assertEqual([(t[2][0], t[3][0]) for t in boss], [(E.CRANES, "sc_e_0405")], "the Cranes, then out")
        self.assertFalse(r.black, "the fight starts from the field")
        r = Run(self.script("sc_e_0405"), st)
        self.assertEqual(r.did("SysCall"), [], "Setzer doesn't join")
        ship = r.did("SetVehicle")
        self.assertEqual([t[2][:4] for t in ship], [[E.BLACKJACK, E.WOB_MAP, 121, 189]], "the Blackjack outside Vector")
        self.assertEqual(r.end, ("ChangeMap", E.WOB_MAP, 27, ""))
        self.assertEqual(st.flag(BLACKJACK_RELEASED), 1)

        vanilla = State()
        self.assertEqual(Run(self.script("sc_e_0404"), vanilla).end, ("ChangeMap", 578, 1, "sc_e_0404_1"))
        self.assertEqual(vanilla.get("ScenarioFlag1", 194), 1, "vanilla keeps its escape flag")

    def test_phantom_train_without_its_story(self):
        # WC event/phantom_train.py: the dungeon (roof fight, jump, lever, meal, Siegfried) with
        # no story scenes and no ghosts joining
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0161"), st)
        self.assertEqual((r.did("Msg"), r.end), ([], ("ChangeMap", 357, 1, "sc_e_0161_1")), "boarding")
        r = Run(self.script("sc_e_0161_1"), st, black=True)
        self.assertEqual(r.did("Msg"), [])
        self.assertEqual([st.get("ScenarioFlag1", f) for f in (108, 109)], [1, 1], "departed; the rear door's scene seen")
        self.assertEqual((st.get("ScenarioFlag4", 167), len(r.did("SetEntities", "ev_e_0163"))), (1, 1))
        self.assertFalse(r.black, "faded in")
        self.assertEqual(Run(self.script("sc_e_0162"), st).end[:2], ("ChangeMap", 624), "the front door leads out at once")
        for name in E.TRAIN_GHOSTS + ["sc_e_0172"]:
            r = Run(self.script(name), st, answers=[0])
            self.assertEqual((r.did("SysCall"), r.offered, r.did("Msg"), r.end), ([], [], [], ("Exit",)), name)
        r = Run(self.script("sc_e_0185_2"), st, black=True)
        self.assertEqual((r.did("Msg"), st.get("ScenarioFlag1", 110), r.black), ([], 1, False), "surrounded at once")
        self.assertEqual(sorted(t[2][1] for t in r.did("SetPuppet") if t[2][1] in [g[0] for g in E.TRAIN_ROOF_GHOSTS]),
                         sorted(g[0] for g in E.TRAIN_ROOF_GHOSTS), "every ghost placed")
        r = Run(self.script("sc_e_0186"), st)
        self.assertEqual((r.did("Msg"), st.get("ScenarioFlag1", 111), len(r.did("SetEntities", "ev_e_0187"))), ([], 1, 1))
        r = Run(self.script("sc_e_0188"), st)
        self.assertEqual((r.did("Msg"), st.get("ScenarioFlag1", 113), r.end), ([], 1, ("ChangeMap", 362, 1, "")))
        r = Run(self.script("sc_e_0189"), st)
        self.assertEqual((r.did("ChangeMap"), st.get("ScenarioFlag1", 114), r.end), ([], 1, ("ChangeScript", "sc_e_0190")),
                         "detached without going outside")
        r = Run(self.script("sc_e_0196"), st)
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["E0196_00_363_a_06"], "only 'HP and MP restored!'")
        self.assertEqual((len(r.did("RecoveryHP")), st.get("ScenarioFlag1", 116)), (1, 1))
        r = Run(self.script("sc_e_0197"), st, answers=[0])
        self.assertEqual((len(r.offered), len(r.did("RecoveryHP")), r.end), (1, 1, ("Exit",)), "later meals: asked, then fed")
        r = Run(self.script("sc_e_0202"), st)
        self.assertEqual((r.did("Msg"), [t[2][0] for t in r.did("EncountBoss")]), ([], [480]), "Siegfried's fight")
        vanilla = State()
        self.assertTrue(Run(self.script("sc_e_0161_1"), vanilla).did("Msg", "E0161_01_357_a_01"), "vanilla keeps the scene")

    def test_doma_dream_without_its_story(self):
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0935_1"), st, black=True)
        self.assertEqual((r.did("Msg"), r.black, st.get("ScenarioFlag2", 195)), ([], False, 1), "arrival: no scene")
        self.assertEqual(len(r.did("SetEntities", "ev_e_0935_1")), 1, "its save point")
        r = Run(self.script("sc_map_30850"), st)
        self.assertEqual(r.did("SetEntities", "ev_e_0936") + r.did("SetEntities", "ev_e_0937"), [], "no flashbacks")
        self.assertEqual(Run(self.script("sc_e_0938"), st).end, ("ChangeMap", 536, 1, ""), "the door is a door")
        r = Run(self.script("sc_e_0939_1"), st, black=True)
        self.assertEqual((r.did("Msg"), r.end), ([], ("ChangeMap", 300, 1, "sc_e_0939_2")), "after Wrexsoul: Cyan wakes")
        self.assertEqual(len(r.did("RecoveryHP")), 1)
        r = Run(self.script("sc_e_0939_2"), st, black=True, syscalls={"WC:gate doma_wor": gate(True)})
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["WC_REWARD"], "the reward only")
        self.assertEqual(len(r.did("SysCall", "WC:reward doma_wor 0")), 1)
        self.assertFalse(r.black)
        r = Run(self.script("sc_e_0895"), st, answers=[0])
        self.assertEqual((r.did("Msg", "E0895_00_300_a_01"), len(r.did("RecoveryHP")), r.end),
                         ([], 1, ("ChangeMap", 520, 1, "sc_e_0895_1")), "rest, then the dream: no Stooges' intro")
        vanilla = State()
        self.assertEqual(len(Run(self.script("sc_map_30850"), vanilla).did("SetEntities", "ev_e_0936")), 1)

    def test_doma_dream_takes_the_party_as_it_is(self):
        # The user's Deck, 2026-11-12: "i rested in wor doma and didn't get the doma dream event"
        # (seed gaadl68ix7j4, Cyan a starting character). The bedroom only offered the dream's bed
        # with Cyan in the party, 4 members and Mt. Zozo done; WC's needs Cyan recruited (its gate)
        def bed(st, gate_open=True):
            r = Run(self.script("sc_map_20451_4"), st, syscalls={"WC:gate doma_wor": gate(gate_open)})
            return [t[3][0] for t in r.did("SetEntities")]

        st, _ = self.new_game()
        st.party = [5, 12]                                    # Edgar and Gau: no Cyan, 2 members
        self.assertEqual(st.get("ScenarioFlag2", 109), 0, "Mt. Zozo not done")
        self.assertEqual(bed(st), ["ev_e_0895"], "the dream's bed")
        self.assertEqual(bed(st, gate_open=False), ["ev_e_0894"], "Cyan not recruited: the inn's")
        done = State()
        done.scenario, done.party = list(st.scenario), [5, 12]
        done.set(E.WC_FLAGS, E.CHECK_FLAGS["doma_wor"], 1)
        self.assertEqual(bed(done), ["ev_e_0894"], "check done")
        done = State()
        done.scenario, done.party = list(st.scenario), [3, 5, 12, 1]
        done.set("ScenarioFlag2", 199, 1)
        self.assertEqual(bed(done), ["ev_e_0894"], "dream done")
        vanilla = State()
        vanilla.party = [5, 12]
        self.assertEqual(bed(vanilla), ["ev_e_0894"], "vanilla keeps its rule")
        vanilla.party = [3, 5, 12, 1]
        vanilla.set("ScenarioFlag2", 109, 1)
        self.assertEqual(bed(vanilla), ["ev_e_0895"])

        # the bed: the party's own slots in the beds, nobody moved to the reserve, no split (it
        # empties a party of 3 with Cyan), both companions found (the Dreamscape's barrier gone)
        r = Run(self.script("sc_e_0895"), st, answers=[0])
        self.assertEqual(r.end, ("ChangeMap", 520, 1, "sc_e_0895_1"))
        calls = [t[3][0] for t in r.did("SysCall")]
        self.assertFalse([c for c in calls if c.startswith("控えに移動") or c.startswith("夢のダンジョン")], calls)
        sc = self.script("sc_e_0895")
        beds = next(t[2][0] for t in r.did("Call") if sc["Mnemonics"][t[2][0]]["label"] == "wc_beds")
        self.assertEqual([m["operands"]["iValues"][1] for m in sc["Mnemonics"][beds + 1:beds + 5]],
                         [-1001, -1002, -1003, -1004], "slots 1-4 in the beds, not Cyan's entity")
        self.assertEqual((st.get("ScenarioFlag2", 181), st.get("ScenarioFlag2", 183)), (1, 1))
        self.assertTrue(r.did("ScreenEffect") and r.black, "the dream's effect, then black")
        r = Run(self.script("sc_map_30821_2"), st)
        self.assertEqual(r.did("SetEntities", "ev_e_0900"), [], "no barrier before the Stooges")
        self.assertEqual(len(r.did("SetEntities", "ev_e_0901")), 1, "the Stooges")
        self.assertEqual(Run(self.script("sc_map_30821_1"), st).did("SysCall"), [], "no companions to find")
        vanilla = State()
        r = Run(self.script("sc_e_0895"), vanilla, answers=[0])
        self.assertEqual([t[3][0] for t in r.did("SysCall")][-2:], ["控えに移動：カイエン", "夢のダンジョン：分離"],
                         "vanilla's bed as it was")

    def test_check_scenes_are_fight_and_reward(self):
        # the user's Deck, 2026-11-12: "there's still a cutscene with edgar in disguise" (Figaro's
        # engine room); WC trims the scenes around its checks' fights and rewards
        def run(name, gate_name=None, **kw):
            st = kw.pop("st", None) or self.new_game()[0]
            sys_ = {f"WC:gate {gate_name}": gate(True)} if gate_name else {}
            return st, Run(self.script(name), st, syscalls=sys_, **kw)

        def story(r):
            return [t[3][0] for t in r.did("Msg") if not t[3][0].startswith("WC_")]

        st, r = run("sc_e_0672", "figaro_castle_wor")
        self.assertEqual((story(r), r.did("SysCall", "メンバー加入：エドガー")), ([], []))
        self.assertEqual([(t[2][0], t[3][0]) for t in r.did("EncountBoss")], [(455, "sc_e_0672_1")], "Gerad: the Tentacles at once")
        st, r = run("sc_e_0672_1", black=True)
        self.assertEqual(([t[3][0] for t in r.did("Msg")], st.get("ScenarioFlag2", 79)), (["WC_REWARD"], 1))
        self.assertEqual(r.end, ("ChangeMap", *E.FIGARO_ENGINE, ""), "the room reloads without the thieves")
        self.assertEqual(r.black_lines, [])

        st, r = run("sc_e_0344_2", black=True)
        self.assertEqual(([t[3][0] for t in r.did("Msg")], r.end[:2]), (["WC_REWARD"], ("ChangeMap", 119)), "opera: reward, deck")
        st, r = run("sc_e_0639", black=True)
        self.assertEqual(([t[3][0] for t in r.did("Msg")], len(r.did("FinishTimer")), r.black), (["WC_REWARD"], 1, False))
        self.assertEqual(r.black_lines, [], "collapsing house: the reward on screen")
        st, r = run("sc_e_0460_3", black=True)
        self.assertEqual(([t[3][0] for t in r.did("Msg")], r.end), (["WC_REWARD"], ("ChangeMap", 411, 2, "sc_e_0460_4")))
        self.assertEqual(r.black_lines, [], "Sealed Gate: the reward on screen")
        st, r = run("sc_e_0110_1", black=True)
        self.assertEqual(([t[3][0] for t in r.did("Msg")], r.end[:3]), (["WC_REWARD"], ("ChangeMap", 75, 1)), "Doma: reward, out")
        self.assertEqual(r.black_lines, [])
        st, r = run("sc_e_0523", "burning_house")
        self.assertEqual((story(r), r.did("SysCall", "メンバー加入：ストラゴス"), r.end),
                         ([], [], ("ChangeMap", 412, 1, "")), "straight into the burning house")
        self.assertEqual(st.get("ScenarioFlag2", 17), 1)
        st, r = run("sc_e_0539_1", black=True)
        self.assertEqual(([t[3][0] for t in r.did("Msg")], r.end), (["WC_REWARD"], ("ChangeMap", 186, 1, "sc_e_0539_3")))
        self.assertEqual(r.black_lines, [])
        st, r = run("sc_e_0539_3", black=True)
        self.assertEqual((story(r), st.get("ScenarioFlag2", 19), r.black, r.end), ([], 1, False, ("Exit",)), "the morning after")
        st, r = run("sc_e_0950_1", black=True)
        self.assertEqual(([t[3][0] for t in r.did("Msg")], r.end), (["WC_REWARD"], ("ChangeMap", 310, 1, "sc_e_0950_6")),
                         "Ebot's Rock: reward, then the end of the Thamasa scenes")
        st, r = run("sc_e_0218_2", black=True)
        self.assertEqual(([t[3][0] for t in r.did("Msg")], st.get("ScenarioFlag1", 125)), (["WC_REWARD"], 1), "Baren Falls: no Gau")
        self.assertEqual(([t[2][0] for t in r.did("Call") if t[2][0] >= 80], r.did("SysCall", "メンバー加入：ガウ")),
                         ([], []), "only the landing's routines, none of Gau's")

    def test_recruit_stand_ins_give_once(self):
        # the user's playtest, 2026-11-12: the Zone Eater's Gogo stand-in could be talked to again
        # after its Minerva Bustier; the second talk ran the vanilla scene (Gogo's naming and join).
        # The stand-in (or the scene's trigger) stays in the room until it reloads.
        joins = {"zone_eater": ("sc_e_0865", "ゴゴ", 1), "umaro_cave": ("sc_e_0790", "ウーマロ", 1),
                 "owzer_mansion": ("sc_e_0775", "リルム", None), "lone_wolf": ("sc_e_0438", "モグ", None)}
        for module, (name, who, npc) in joins.items():
            with self.subTest(module):
                st, _ = self.new_game()
                st.party = [1, 2, 11]                         # Mog along (Umaro's vanilla scene wants him)
                sc = self.script(name)
                talk = lambda: Run(sc, st, syscalls={f"WC:gate {module}": gate(True)})
                r = talk()
                self.assertEqual(len(r.did("SysCall", f"WC:reward {module} 0")), 1)
                self.assertEqual(st.get(E.WC_FLAGS, E.CHECK_FLAGS[module]), 1)
                hidden = [t[2][1] for t in r.did("SetPuppet")
                          if sc["Mnemonics"][t[2][2] + 1]["mnemonic"] == "Hide"]
                self.assertEqual(hidden, [npc] if npc else [], "the stand-in leaves (WC deletes it)")
                again = talk()
                self.assertEqual((again.did("SysCall"), again.did("Msg"), again.end), ([], [], ("Exit",)),
                                 f"done: nothing (not {who}'s vanilla join)")
                self.assertEqual(again.did("SysCall", "メンバー加入：" + who), [])
        st, _ = self.new_game()
        Run(self.script("sc_e_0735"), st, syscalls={"WC:gate mt_zozo": gate(True)})
        again = Run(self.script("sc_e_0735"), st, syscalls={"WC:gate mt_zozo": gate(True)})
        self.assertEqual((again.did("SysCall"), again.end), ([], ("ChangeMap", 475, 1, "")),
                         "Mt. Zozo done: the vanilla door into Cyan's room, no scene")

    def test_umaro_cliff_needs_umaro(self):
        # WC event/umaro_cave.py add_gating_condition: without Umaro, Tritoch's cliff only says
        # "There's an opening in the cliff." and walks the party back; no jump (the user, Deck,
        # 2026-11-12: the port let the party in and gated only the reward after Umaro's battle)
        st, _ = self.new_game()
        sc = self.script("sc_e_0781")
        closed = Run(sc, st, answers=[0], syscalls={"WC:gate umaro_cave": gate(False)})
        self.assertEqual(([t[3][0] for t in closed.did("Msg")], closed.offered, closed.end),
                         (["WC_UMARO_CLIFF"], [], ("Exit",)))
        self.assertEqual([t[2][0] for t in closed.did("Call")], [27], "the party steps back (return_move)")
        jump = Run(sc, st, answers=[0], syscalls={"WC:gate umaro_cave": gate(True)})
        self.assertEqual(([t[3][0] for t in jump.did("Msg")], jump.end),
                         (["E0781_00_020_a_01"] * 2, ("ChangeMap", 493, 1, "sc_e_0781_1")), "Umaro joined: the vanilla jump")
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["umaro_cave"], 1)
        again = Run(sc, st, answers=[0], syscalls={"WC:gate umaro_cave": gate(True)})
        self.assertEqual(again.end[:2], ("ChangeMap", 493), "check done: the cave stays open, as WC")
        vanilla = Run(sc, State(), answers=[0])
        self.assertEqual((vanilla.did("SysCall"), vanilla.end[:2]), ([], ("ChangeMap", 493)), "no WC game: untouched")

    def test_narshe_snowfield_has_the_ice_dragon_in_the_wor(self):
        # the user's Deck, 2026-11-12: "i didn't see the ice dragon in narshe". The snowfield's WoR
        # block (save point, Ice Dragon) sits past the WoB's Narshe battle flags (ScenarioFlag1
        # 136-138) that a WC game never sets
        sc = self.script("sc_map_20030")
        groups = lambda st: [t[3][0] for t in Run(sc, st).did("SetEntities")]
        st, _ = self.new_game()
        self.assertIn("ev_e_0250", groups(st), "the WoB before the Narshe battle: Banon waits")
        self.assertNotIn("ev_e_0779", groups(st))
        st.set(*COLLAPSED, 1)
        st.set(*GOT_FALCON, 1)
        wor = groups(st)
        self.assertIn("ev_e_0703", wor, "the WoR save point")
        self.assertIn("ev_e_0779", wor, "the Ice Dragon")
        self.assertNotIn("ev_e_0250", wor, "no Banon in the WoR")
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["narshe_battle"], 1)
        self.assertIn("ev_e_0779", groups(st), "with the Narshe battle done too")
        st.set("ScenarioFlag2", 138, 1)                           # the Ice Dragon beaten
        self.assertNotIn("ev_e_0779", groups(st))
        self.assertIn("ev_e_0703", groups(st))
        vanilla = State()
        vanilla.set(*COLLAPSED, 1)
        vanilla.set(*GOT_FALCON, 1)
        self.assertNotIn("ev_e_0779", groups(vanilla), "no WC game: vanilla's gate on the Narshe battle")
        for f in (136, 137, 138):
            vanilla.set("ScenarioFlag1", f, 1)
        self.assertIn("ev_e_0779", groups(vanilla))

    def test_mines_1f_has_no_opening_walls(self):
        # the user's Deck, 2026-11-12: the WoR mines 1F's east tunnel couldn't be entered. A WC game
        # leaves ScenarioFlag1 13 clear (the Whelk loads here), which also put up the opening's
        # Magitek walls (ev_e_0001), one of them on the bridge before the tunnel's mouth
        sc = self.script("sc_map_30021_1")
        for world in (0, 1):
            with self.subTest(wor=world):
                st, _ = self.new_game()
                st.set(*COLLAPSED, world)
                r = Run(sc, st)
                groups = [t[3][0] for t in r.did("SetEntities")]
                self.assertNotIn("ev_e_0001", groups, "no Magitek walls in a WC game")
                self.assertIn("ev_e_0609_1" if world else "ev_e_0609", groups, "the floor's exits")
                self.assertIn("ev_e_0011", groups, "the Whelk check still loads")
        vanilla = State()
        self.assertIn("ev_e_0001", [t[3][0] for t in Run(sc, vanilla).did("SetEntities")], "the opening: untouched")

    def test_phoenix_cave_has_no_reunion(self):
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0828"), st, syscalls={"WC:gate phoenix_cave": gate(True)})
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["WC_REWARD"])
        self.assertEqual(r.end, ("ChangeMap", 268, 2, ""))

    def test_towers_without_their_story(self):
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0594"), st)
        self.assertEqual((r.did("Msg"), r.end), ([], ("ChangeMap", 631, 4, "sc_e_0594_2")), "statues: straight to the escape")
        self.assertEqual(Run(self.script("sc_e_0966_1"), st).end, ("ChangeScript", "sc_e_0966_3"), "no flight round the tower")
        r = Run(self.script("sc_e_0966_3"), st)
        self.assertEqual((r.did("Msg"), r.end), ([], ("ChangeMap", 584, 1, "sc_e_0966_3_1")), "no talk: the three parties")
        vanilla = State()
        self.assertEqual(Run(self.script("sc_e_0594"), vanilla).end[3], "sc_e_0594_b")
        self.assertEqual(Run(self.script("sc_e_0966_1"), vanilla).end[3], "sc_e_0966_2")

    def test_wor_scenes_on_the_way_to_checks(self):
        st, _ = self.new_game()
        st.set(*COLLAPSED, 1)
        r = Run(load(self.built_path("sc_e_0667", "Map_30641_2")), st)
        self.assertEqual((r.end, st.get("ScenarioFlag2", 76)), (("ChangeMap", 448, 1, ""), 1), "the turtle already shuttles")
        r = Run(self.script("sc_map_30641_7"), st)
        self.assertEqual(r.did("SetEntities", "ev_e_0670"), [], "Siegfried doesn't run off")
        r = Run(self.script("sc_e_0707"), st)
        self.assertEqual((r.did("Msg"), st.get("ScenarioFlag2", 99), len(r.did("SetEntities", "ev_e_0709"))), ([], 1, 1))
        st.party = [6, 12, 1]                    # Sabin and Gau
        r = Run(self.script("sc_e_0959"), st)
        self.assertEqual((r.did("ChangeMap"), r.end), ([], ("Exit",)), "no repairman scene")
        self.assertEqual(Run(self.script("sc_e_0028"), st).end, ("ChangeScript", "sc_npc_20051_6_1"), "no twins flashback")
        r = Run(self.script("sc_e_0709_3"), st, black=True)
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["WC_REWARD"], "Mobliz: the reward, no scene")
        self.assertEqual((st.get("ScenarioFlag2", 100), r.black), (1, False))

    def built_path(self, name, folder):
        hits = [p for p in glob.glob(os.path.join(self.tmp, "**", name + ".json"), recursive=True) if folder in p]
        assert len(hits) == 1, (name, folder, hits)
        return hits[0]

    def test_freeing_celes_is_the_reward_at_once(self):
        # WC event/south_figaro.py: talking to her gives the reward; no question, no scene (the
        # user's playtest, 2026-11-06: the vanilla lines and "(Remove her bindings.)" showed)
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0079"), st, syscalls={"WC:gate south_figaro": gate(True)})
        self.assertEqual(r.offered, [], "no question")
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["WC_REWARD"], "no story line, only the reward's")
        self.assertEqual(len(r.did("SysCall", "WC:reward south_figaro 0")), 1)
        self.assertEqual(r.did("SysCall", "メンバー加入：セリス"), [], "Celes doesn't join (the seed's reward does)")
        self.assertEqual(st.get("ScenarioFlag1", 59), 1, "freed")
        self.assertEqual(st.get(E.WC_FLAGS, E.CHECK_FLAGS["south_figaro"]), 1, "the check done")
        self.assertEqual(r.did("RecoveryHP"), [], "no heal (WC has none)")
        self.assertEqual(r.end, ("Exit",))
        # the gate closed (Celes not recruited): nothing happens
        st2, _ = self.new_game()
        r2 = Run(self.script("sc_e_0079"), st2, syscalls={"WC:gate south_figaro": gate(False)})
        self.assertEqual((r2.offered, r2.did("Msg"), st2.get("ScenarioFlag1", 59)), ([], [], 0))
        # outside a WC game: the vanilla scene and its question
        st3 = State()
        r3 = Run(self.script("sc_e_0079"), st3)
        self.assertEqual(r3.offered, [["E0079_01_050_c_01", "E0079_01_050_c_02"]])

    def test_imperial_camp_lands_on_the_world_map_faded_in(self):
        # the user's playtest (2026-11-06): after Cyan's third fight the screen stayed black (the
        # reward's jump to the world map had no arrival script; this script starts before its
        # own fade-in from the battle). It leaves as vanilla's camp exit does (sc_e_0133)
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0128_1"), st)
        self.assertEqual(len([t for t in r.did("SysCall") if t[3][0].startswith("WC:reward imperial_camp")]), 1)
        self.assertEqual([st.get("ScenarioFlag1", f) for f in (93, 94, 95, 96)], [1] * 4, "the escape's steps done")
        # not its end (97): the world map keeps the camp (its 4 chests; WC lets the party back in)
        self.assertEqual(st.get("ScenarioFlag1", 97), 0, "the camp stays on the world map")
        world = self.script("sc_map_10010")["Mnemonics"]
        at = next(i for i, m in enumerate(world) if m["mnemonic"] == "SetEntities" and m["operands"]["sValues"][0] == "ev_e_0133")
        gate97 = world[at - 1]
        self.assertEqual((gate97["mnemonic"], gate97["operands"]["iValues"][:2], gate97["operands"]["sValues"][0]),
                         ("Branch", [97, 1], "ScenarioFlag1"), "the world map's camp entrance hangs on 97 alone")
        self.assertEqual(r.end, ("ChangeMap", 1, 11, "sc_e_0133_1"))
        arrive = [m["mnemonic"] for m in self.script("sc_e_0133_1")["Mnemonics"]]
        self.assertIn("FadeIn", arrive, "the arrival fades the world map in")

    def test_imperial_camp_walk_to_cyans_duels(self):
        # WC: the trigger at (36, 23), down the camp's middle, runs "Cyan rushes in", which puts
        # the party by Cyan's duels at (8, 25); the user (2026-11-06) walked across the whole PR
        # camp to them instead. The PR's trigger on that tile (Leo's talk layer) does it now
        def walk_set(r):
            return [t for t in r.did("SetEntities") if t[3][0] == E.CAMP_WALK]
        gates = {"WC:gate imperial_camp": gate(True)}
        st, _ = self.new_game()
        r = Run(self.script("sc_map_30410"), st, syscalls=gates)
        self.assertEqual(len(walk_set(r)), 1, "the trigger's layer set")
        hidden = sorted(t[2][1] for t in r.did("SetPuppet") if t[2][1] in E.CAMP_WALK_NPCS)
        self.assertEqual(hidden, sorted(E.CAMP_WALK_NPCS), "its Leo and soldiers hidden")
        self.assertEqual(st.get("ScenarioFlag1", 90), 1, "Cyan's duels set up")
        self.assertTrue(r.did("SetEntities", E.CAMP_CHEST), "the 4th chest there (WC sets its bit at the start)")
        # never with the check done, the gate closed, or outside a WC game
        done, _ = self.new_game()
        done.set(E.WC_FLAGS, E.CHECK_FLAGS["imperial_camp"], 1)
        r_done = Run(self.script("sc_map_30410"), done, syscalls=gates)
        self.assertEqual(walk_set(r_done), [])
        # the check done: the camp as it stands (its chests and exits, its music), no scene
        self.assertTrue(r_done.did("SetEntities", E.CAMP_CHEST) and r_done.did("PlayBGM"))
        self.assertEqual([t[3][0] for t in r_done.did("SetEntities")], [E.CAMP_CHEST], "no scene's layer")
        self.assertEqual(done.get("ScenarioFlag1", 90), 0, "not the duels' state")
        closed, _ = self.new_game()
        r_closed = Run(self.script("sc_map_30410"), closed, syscalls={"WC:gate imperial_camp": gate(False)})
        self.assertEqual(walk_set(r_closed), [])
        self.assertTrue(r_closed.did("SetEntities", E.CAMP_CHEST))
        vanilla = Run(self.script("sc_map_30410"), State())
        self.assertEqual(walk_set(vanilla), [])
        self.assertFalse(vanilla.did("SetEntities", E.CAMP_CHEST), "vanilla's first visit: not yet")
        # stepping on it: the party to the duels, once a visit; no Leo talk in a WC game
        walk = self.script("sc_e_0114")
        ms = walk["Mnemonics"]
        r = Run(walk, st)
        moved = [t for t in r.did("SetPuppet") if t[2][1] == -1001]
        self.assertEqual(len(moved), 1, "the party moved")
        nxt = ms[moved[0][2][2] + 1]
        self.assertEqual((nxt["mnemonic"], nxt["operands"]["iValues"][:2]), ("SetPos", list(E.CYAN_DUELS)))
        self.assertEqual(r.did("Msg"), [], "no Leo talk")
        self.assertFalse(r.black, "faded back in")
        self.assertEqual([t for t in Run(walk, st).did("SetPuppet") if t[2][1] == -1001], [], "not twice a visit")
        self.assertTrue(Run(walk, State()).did("Msg", "E0114_00_350_a_01"), "outside a WC game: Leo's talk")

    def test_no_reward_line_on_a_black_screen(self):
        """A reward script that starts on a black screen (vanilla fades in before its first line:
        after a battle or a map change) shows WC's reward line only after a fade-in: on black it
        waits for a press nobody can tell it wants (the user's playtest, 2026-11-06: the Imperial
        Camp's reward after Cyan's third fight, "the screen just went black")."""
        class OpenGates(dict):
            def get(self, k, d=None):
                return gate(True) if k.startswith("WC:gate") else d
        reached, bad = [], []
        for p in sorted(self.built.values()):
            built = load(p)
            if not any(m["mnemonic"] == "SysCall" and str(m["operands"]["sValues"][0]).startswith("WC:reward")
                       for m in built["Mnemonics"]):
                continue
            name = os.path.basename(p)[:-5]
            try:
                van = load(S.path(name))
            except Exception:
                continue
            # black at the start when vanilla fades in before anything shows (after a battle or
            # a map change); every one is run (a scene may fade out before its reward, too)
            first = next((m["mnemonic"] for m in van["Mnemonics"] if m["mnemonic"] in ("FadeIn", "FadeOut", "Msg")), None)
            st, _ = self.new_game()
            try:
                r = Run(built, st, syscalls=OpenGates(), black=first == "FadeIn")
            except AssertionError:
                continue                                    # waits on something the model doesn't run
            if any(t[3][0] == "WC_REWARD" for t in r.did("Msg")):
                reached.append(name)
            if "WC_REWARD" in r.black_lines:
                bad.append(name)
        self.assertIn("sc_e_0128_1", reached, "the Imperial Camp's reward is among those run")
        self.assertEqual(bad, [], f"reward lines on a black screen ({len(reached)} reward scripts reached their reward)")

    def test_kefka_tower_demon_drop(self):
        """WC's -fbs: the ledge behind Kefka's Tower's Demon statue (sc_e_0968) sets the party back a
        tile until the Demon is beaten (ScenarioFlag2 238); beaten, or without -fbs, the drop."""
        def run(fbs, beaten):
            st, _ = self.new_game()
            st.set(*E.DEMON_BEATEN, beaten)
            sc = self.script("sc_e_0968")
            r = Run(sc, st, syscalls={"WC:has -fbs": lambda s: s.set(E.WC_FLAGS, E.SCRATCH, 1 if fbs else 0)})
            # the model starts puppets without running them: each routine's first move (it runs from
            # the label's next op)
            ms = sc["Mnemonics"]
            return [(ms[t[2][2] + 1]["mnemonic"], tuple(ms[t[2][2] + 1]["operands"]["iValues"][:2]))
                    for t in r.trace if t[1] == "SetPuppet"]
        self.assertEqual(run(True, 0), [("Move", (0, 1))] * 2, "set back north, the party and its follower")
        self.assertEqual(run(True, 1), [("Move", (2, 2))] * 2, "beaten: the drop")
        self.assertEqual(run(False, 0), [("Move", (2, 2))] * 2, "no -fbs: the drop")

    def test_party_pick_after_every_reward(self):
        """WC's RecruitAndSelectParty: every reward clears the pick flag before WC:reward and, the
        flag set (a character joined), opens the PR's one-party select screen, then redraws the
        leader where the party stands (WC:party_refresh): no map change, which would cut the
        check's scene short (the user's list, 2026-11-06)."""
        bad, n = [], 0
        for p in sorted(self.built.values()):
            ms = load(p)["Mnemonics"]
            name = os.path.basename(p)[:-5]
            for i, m in enumerate(ms):
                if m["mnemonic"] != "SysCall" or not str(m["operands"]["sValues"][0]).startswith("WC:reward"):
                    continue
                n += 1
                prev = ms[i - 1] if i else {}
                if prev.get("mnemonic") != "ResetFlag" or prev["operands"]["iValues"][0] != E.PICK_PARTY:
                    bad.append(f"{name}[{i}]: the pick flag isn't cleared before the reward")
                after = ms[i + 1:i + 30]
                end = next((k for k, x in enumerate(after) if x["mnemonic"] == "SysCall" and x["operands"]["sValues"][0] == "WC:party_refresh"), None)
                calls = [x["operands"]["sValues"][0] for x in after[:end] if x["mnemonic"] == "SysCall"]
                if end is None or E.PARTY_MENU not in calls or not any(
                        x["mnemonic"] == "Branch" and x["operands"]["iValues"][0] == E.PICK_PARTY for x in after[:end]) \
                        or any(x["mnemonic"] == "ChangeMap" for x in after[:end]):
                    bad.append(f"{name}[{i}]: no party pick after the reward")
        self.assertGreater(n, 40)
        self.assertEqual(bad, [])

    def test_narshe_checkpoint_without_its_explanation(self):
        # WC keeps Narshe's security checkpoint but deletes its explanation (event/narshe_wob.py:
        # CC/DA09 returns); the user's playtest (2026-11-06) saw Edgar's and Terra's lines
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0090"), st)
        self.assertEqual(r.did("Msg"), [], "no explanation")
        self.assertTrue(any(t[1] == "Call" and t[2][0] == 23 for t in r.trace), "the route of light shows")
        self.assertEqual(st.get("MapLocalVariable", 1), 1, "the trial under way")
        self.assertEqual(st.get(E.WC_FLAGS, E.CHECKPOINT_STARTED), 1, "the quest's attempt started")
        # outside a WC game: vanilla's lines the first time
        r2 = Run(self.script("sc_e_0090"), State())
        self.assertEqual([t[3][0] for t in r2.did("Msg")], ["E0090_00_320_a_01", "E0090_00_320_a_02", "E0090_00_320_a_03"])

    # --- flags -------------------------------------------------------------------------------
    def test_flag_segments_alias(self):
        """The trap itself: a scenario flag index past 255 is the next segment's flag."""
        st = State()
        st.set("ScenarioFlag1", 297, 1)
        self.assertEqual(st.flag(COLLAPSED), 1)

    def test_wc_scripts_use_flags_in_their_segment(self):
        """Every flag the WC build adds to a script (not vanilla's own) has an index under 256."""
        bad = []
        for name, p in self.built.items():
            wc = load(p)
            try:
                van = S.load(wc["Name"])
            except Exception:
                van = {"Mnemonics": []}

            def flags(sc):
                return collections.Counter((m["mnemonic"], m["operands"]["sValues"][0], m["operands"]["iValues"][0])
                                           for m in sc["Mnemonics"]
                                           if m["mnemonic"] in ("SetFlag", "ResetFlag", "Branch")
                                           and m["operands"]["sValues"][0] in {**SCENARIO, **TREASURE})
            added = flags(wc) - flags(van)
            bad += [(name, k) for k in added if k[2] >= SEGMENT]
        self.assertEqual(bad, [])
        consts = list(E.START_FLAGS) + [(E.WC_FLAGS, i) for i in E.CHECK_FLAGS.values()] + [COLLAPSED, GOT_FALCON]
        self.assertEqual([f for f in consts if not 0 <= f[1] < SEGMENT], [])

    def test_plugin_flag_indices(self):
        """The plugin's flag reads/writes with a literal index stay in their segment, and nothing
        writes Morph's constant (TranceAdditionalScenarioFlagId, 297) as a ScenarioFlag1 index."""
        code = []
        for p in glob.glob(os.path.join(PR, "plugin", "WcPlugin", "*.cs")):
            with open(p, encoding="utf-8") as f:
                code += [re.sub(r"//.*", "", line) for line in f]     # comments may name things
        src = "".join(code)
        lits = [int(n) for n in re.findall(r"k(?:Scenario|Treasure)Flag[1-4]\s*,\s*(\d+)", src)]
        self.assertEqual([n for n in lits if n >= SEGMENT], [])
        self.assertFalse("TranceAdditionalScenarioFlagId" in src,
                         "the plugin uses Morph's flag constant: 297 is ScenarioFlag2 41 (after the collapse)")


class WcPort(unittest.TestCase):
    """The port of a real WC seed (WC run on the vanilla ROM, seed "unittest"): chests and shops."""

    @classmethod
    def setUpClass(cls):
        warnings.simplefilter("ignore", ResourceWarning)
        import subprocess
        import prdata as P
        import ff6snes as SN
        cls.tmp = tempfile.mkdtemp(prefix="wc_port_")
        rom = os.path.join(cls.tmp, "wc.sfc")
        wc_dir = os.path.join(PR, "..", "mods", "worlds-collide")
        flags = json.load(open(os.path.join(PR, "tools", "..", "..", "mods", "worlds-collide-presets.json"), encoding="utf-8"))["moogle cup"]
        p = subprocess.run([sys.executable, os.path.join(PR, "tools", "wc_run.py"), os.path.join(cls.tmp, "wc_events.json"),
                            "-i", P.VANILLA_ROM, "-o", rom, "-s", "unittest"] + flags.split(),
                           cwd=wc_dir, capture_output=True, text=True, encoding="utf-8", errors="replace")
        if p.returncode != 0 or not os.path.exists(rom):
            raise unittest.SkipTest(f"WC didn't run: {(p.stderr or p.stdout)[-300:]}")
        cls.van, cls.wc = SN.Rom(P.VANILLA_ROM), SN.Rom(rom)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def test_chest_messages_follow_contents(self):
        """Every chest the port rewrites says what it holds (an item chest that had been gil said
        "Obtained (NUM) gil!")."""
        import port_chests as PC
        out = os.path.join(self.tmp, "chests")
        PC.port(self.van, self.wc, out, lambda *a: None)
        import prdata as P

        def chests(path):
            out_ = {}
            for layer in load(path).get("layers", []):
                for o in layer.get("objects", []) or []:
                    pp = {x["name"]: x["value"] for x in o.get("properties", [])}
                    if pp.get("action_id") == 6 and "content_id" in pp:
                        out_[o["id"]] = pp
            return out_
        n, bad = 0, []
        for f in glob.glob(os.path.join(out, "**", "entity_default.json"), recursive=True):
            van = chests(os.path.join(P.VANILLA, os.path.relpath(f, out)))
            for oid, pp in chests(f).items():
                v = van.get(oid, {})
                if (v.get("content_id"), v.get("content_num")) != (pp["content_id"], pp.get("content_num")):   # the port's
                    n += 1
                    want = PC.chest_message(pp["content_id"], pp.get("content_num", 0))
                    if pp.get("message_key") != want:
                        bad.append((os.path.basename(os.path.dirname(f)), oid, pp["content_id"], pp.get("content_num"), pp.get("message_key")))
        self.assertGreater(n, 50, "the seed's changed chests")
        self.assertEqual(bad, [])

    def test_shop_copies_stay_with_their_shop(self):
        """A town's two copies of a shop (South Figaro WoR's item shop: PR 67 and 92) both get its
        stock; a weaker match elsewhere doesn't take one."""
        import port
        port.port_shops(self.van, self.wc, {})
        pairs = port.port_shops.pairs
        self.assertIn((63, 67), pairs)
        self.assertIn((63, 92), pairs)

    def test_randomized_discount_shops_reprice_their_new_items(self):
        """A discounted PR product row carries an absolute adjustment, not a percentage.  Once
        WC replaces its item, the adjustment must follow the new price (and can never go negative)."""
        import port
        import prdata as P
        tables = {}
        changed = port.port_shops(self.van, self.wc, tables)
        product = tables["product"]
        content = tables["content"]
        sale = {1: tables["item"], 2: tables["weapon"], 3: tables["armor"]}
        discounted = [r for r in product if 83 <= int(r["group_id"]) <= 92]
        self.assertTrue(discounted)
        for r in discounted:
            c = content[int(r["content_id"])]
            buy = int(sale[int(c["type_id"])][int(c["type_value"])]["buy"])
            cost = buy + int(r["coefficient"])
            self.assertEqual(cost, buy // 2, (r["group_id"], r["content_id"], buy, r["coefficient"]))
            self.assertGreaterEqual(cost, 0)


    def test_ability_scaling_placeholders_are_picked_in_battle(self):
        """WC's ability scaling (-ase, in the preset): each placeholder in WC's scripts compiles to
        its own ability row (7600 + k) that the plugin swaps at cast time, never to one fixed spell
        picked from the monster's unscaled level (the user's Sandhorse always cast Wind Slash,
        2026-11-11); the seed carries WC's tiers as PR abilities."""
        import ai_compile as AC
        import port_ai
        import prdata as P
        import snes_ai as A
        tiers = os.path.join(self.tmp, "wc_events_tiers.json")
        self.assertTrue(os.path.exists(tiers), "WC's run wrote its ability tiers")
        old = os.environ.get("WC_AI_TIERS")
        os.environ["WC_AI_TIERS"] = tiers
        try:
            tables = {}
            out = os.path.join(self.tmp, "ai")
            port_ai.port(self.van, self.wc, tables, out, lambda *a: None)
            table = AC.plugin_table()
        finally:
            if old is None:
                os.environ.pop("WC_AI_TIERS", None)
            else:
                os.environ["WC_AI_TIERS"] = old
        ab = tables["ability"]
        for k in range(36):
            self.assertIn(7600 + k, ab.rows, f"placeholder row {7600 + k}")
        # the Sandhorse (SNES 95): WC's Wind Slash became WIND1 (0x43) -> 7613 in its compiled script
        main, counter = A.script(self.wc, 95)
        doc = load(os.path.join(out, "monster_ai", *port_ai.AI_PATH.split("/"), "sc_ai_bnw_095.json"))
        acts = [i for m in doc["Mnemonics"] if m["mnemonic"].endswith("Act") for i in m["operands"]["iValues"]]
        self.assertIn(7613, acts)
        self.assertNotIn(172, acts, "no fixed Wind Slash left")
        self.assertEqual(table["base"], 7600)
        self.assertEqual(len(table["tiers"]), 12)
        self.assertEqual(table["tiers"][4][3], [172, 176], "wind tier 3: Wind Slash, Sand Storm")
        self.assertTrue(all(all(t) for cat in table["tiers"] for t in cat), "every tier has PR abilities")


class EventLooks(unittest.TestCase):
    def test_event_battles_take_free_ids_and_the_events_background(self):
        # wc_seed.event_looks: a swapped event battle fights a copy of its target on the event's
        # background, at an id nothing else holds (the objectives test seed, 2026-11-06: a
        # formation fought in 6 area looks reached the old event range, and the seed failed)
        import wc_seed
        bg = "battle_background_asset_id"

        class Table:
            def __init__(self, rows):
                self.rows = rows
        rows = {10: {"id": "10", bg: "1"}, 20: {"id": "20", bg: "2"}, 30: {"id": "30", bg: "1"},
                wc_seed.EVENT_LOOK_BASE: {"id": str(wc_seed.EVENT_LOOK_BASE), bg: "9"}}
        tmp = tempfile.mkdtemp(prefix="wc_looks_")
        try:
            remap, n = wc_seed.event_looks({"10": [20, 30]}, Table(rows), tmp)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        self.assertEqual(n, 1, "one copy: 30 already has the event's background")
        copy = next(t for t in remap["10"] if t != 30)
        self.assertNotIn(copy, (10, 20, 30, wc_seed.EVENT_LOOK_BASE))
        self.assertEqual((rows[copy][bg], rows[copy]["id"]), ("1", str(copy)))
        self.assertEqual(rows[20][bg], "2", "the target itself unchanged")

    def test_the_cultists_tower_script_goes_with_the_place(self):
        # the user's Deck seed gaadl68ix7j4, 2026-11-12: MagiMaster at Tritoch's cliff kept the
        # tower's battle script (magic-only status): "i was unable to use attack". The SNES's rule
        # is the tower's event bit, so the tower's script follows the event, not the boss
        import wc_seed
        bg, sn = "battle_background_asset_id", "script_name"

        class Table:
            def __init__(self, rows):
                self.rows = rows
        rows = {355: {"id": "355", bg: "46", sn: "9024"},     # MagiMaster (the tower top)
                482: {"id": "482", bg: "5", sn: "9024"},      # the White Dragon (in the tower)
                488: {"id": "488", bg: "19", sn: "0"},        # Tritoch
                367: {"id": "367", bg: "19", sn: "0"},        # the Ice Dragon
                460: {"id": "460", bg: "32", sn: "9013"},     # Air Force (its own script)
                371: {"id": "371", bg: "5", sn: "0"}}         # a dragon on the White Dragon's background
        tmp = tempfile.mkdtemp(prefix="wc_looks_")
        try:
            remap, n = wc_seed.event_looks({"488": [355], "367": [482], "355": [460], "482": [371]},
                                           Table(rows), tmp)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        got = {old: rows[ts[0]] for old, ts in remap.items()}
        self.assertEqual((got["488"][sn], got["488"][bg]), ("0", "19"), "MagiMaster on the cliff: no tower status")
        self.assertEqual(got["367"][sn], "0", "the White Dragon at the Ice Dragon's: none")
        self.assertEqual(got["355"][sn], "9013", "Air Force at the tower top keeps its routine (the plugin adds the status)")
        self.assertNotEqual(remap["482"], [371], "same background, but the tower's script: a copy")
        self.assertEqual((got["482"][sn], got["482"][bg]), ("9024", "5"), "a dragon in the tower: magic only")
        self.assertEqual((rows[355][sn], rows[482][sn], rows[371][sn]), ("9024", "9024", "0"), "targets unchanged")
        self.assertEqual(n, 4)


class WcAbilityTiers(unittest.TestCase):
    """WC's tier formula (enemy_script_custom_commands.py: get_random_tiered_spell and
    get_tier_distortion, step by step) and the grid the plugin's in-game check
    (WcTest ability_scaling) holds WcAbilityScaling.Tier to."""
    GRID = ("00012 00123 01234 00012 00123 01234 00123 01234 12345 00123 01234 12345 01234 12345 23456 12345 23456 "
            "34567 34567 45678 56789 56789 6789a 789ab 6789a 789ab 89abb 6789a 789ab 89abb 6789a 789ab 89abb 6789a "
            "789ab 89abb 00012 00123 01234 00012 00123 01234 00012 00123 01234 00123 01234 12345 00123 01234 12345 "
            "01234 12345 23456 23456 34567 45678 56789 6789a 789ab 56789 6789a 789ab 56789 6789a 789ab 6789a 789ab "
            "89abb 6789a 789ab 89abb")

    @staticmethod
    def tier(level, off, factor, roll):
        th = [0x00, 0x19, 0x59, 0xb2, 0xf2]
        per = factor + 3
        a = 8 if level >= int(per * 8) else ((level * 2) & 0xFF) // int(per * 2)
        ee = a + off
        x = 4
        while roll < th[x]:
            x -= 1
        a = max(x + ee, 2) - 2
        return min(a, 11)

    def test_grid(self):
        got = []
        for f in (2.0, 2.5):
            for lv in (1, 4, 5, 9, 10, 16, 27, 39, 40, 43, 44, 99):
                for off in (0, 1, 2):
                    got.append("".join("%x" % self.tier(lv, off, f, r) for r in (0, 0x19, 0x59, 0xb2, 0xf2)))
        self.assertEqual(" ".join(got), self.GRID)
        src = open(os.path.join(PR, "plugin", "WcPlugin", "WcTest.cs"), encoding="utf-8").read()
        self.assertIn(self.GRID, src, "the plugin's in-game check holds the same grid")

    def test_placeholder_ids(self):
        import ai_compile as AC
        path = os.path.join(tempfile.mkdtemp(prefix="wc_tiers_"), "t.json")
        json.dump({"factor": 2.0, "random": False, "tiers": {"wind": [[0xE6]] * 3 + [[0x65, 0x69]] * 9}}, open(path, "w"))
        old = os.environ.get("WC_AI_TIERS")
        os.environ["WC_AI_TIERS"] = path
        try:
            self.assertEqual([AC.scaled_index(x) for x in (0x36, 0x43, 0x50, 0x51, 0x54, 0x55, 0x5D, 0x5E)],
                             [0, 13, 26, None, None, 27, 35, None])
            ctx = AC.Context(95, 1, 999)
            self.assertEqual(ctx.ability(0x43), 7613)
            self.assertEqual(ctx.ability(0x5E), 999, "WC's special tiers: the monster's own special")
        finally:
            if old is None:
                os.environ.pop("WC_AI_TIERS", None)
            else:
                os.environ["WC_AI_TIERS"] = old
        self.assertIsNone(AC.scaled_index(0x43), "no WC tiers: a plain spell id")


class WcAtbModel(unittest.TestCase):
    """The SNES's ATB fill (C2/09D2 CalcSpeed: (Speed + 20) x k / 16 per 2 frames, k = 96 / Haste
    126 / Slow 48, monsters x (255 - 24 x battle speed) / 256 first; C2/111B: a gauge of 65536) as
    the PR's gauge-% per second, and the numbers the plugin's in-game check (WcTest atb) holds
    WcAtb to."""

    @staticmethod
    def rate(speed, monster=False, bs=2, k=96):
        s = (speed + 20) & 0xFF
        if monster:
            s = s * (255 - 24 * bs) >> 8
        return 60.0988 / 2 * (s * k >> 4) / 65536 * 100

    def test_numbers_in_the_check(self):
        src = open(os.path.join(PR, "plugin", "WcPlugin", "WcTest.cs"), encoding="utf-8").read()
        speeds = (1, 10, 30, 50, 100, 128)
        rows = {"party": [self.rate(x) for x in speeds]}
        for pr_speed, bs in enumerate((5, 4, 2, 1, 0)):
            rows[f"[{pr_speed}]"] = [self.rate(x, True, bs) for x in speeds]
        for key, vals in rows.items():
            text = ", ".join(f"{round(v, 4):g}" for v in vals)
            self.assertIn(text, src, f"{key}: {text}")
        self.assertIn(f"{round(self.rate(50, k=126), 4):g}", src)
        self.assertIn(f"{round(self.rate(50, k=48), 4):g}", src)
        plugin = open(os.path.join(PR, "plugin", "WcPlugin", "WcAtb.cs"), encoding="utf-8").read()
        self.assertIn("{ 5, 4, 2, 1, 0 }", plugin, "the PR's battle speeds 0-4 -> SNES Bat.Speed 6, 5, 3, 2, 1")


class WcStatusTickModel(unittest.TestCase):
    """The SNES's status clock (WcStatusClock.cs) against the vanilla ROM's code: battle time every 2
    frames (C2/111B), one of 16 slots visited a move with fighters 0-9 (C2/5A83), the counter's add
    64 / Haste 84 / Slow 32 (C2/09D2), the 8-step table Regen-Sap/Poison (C2/5AEA), the random step
    index at the battle's start (C2/2457), and Stop holding everything but its own count (C2/5AA3).
    With FF6_SNES_TESTS=1 the ticks are also measured on the SNES (tools/snes_status_ticks.py, the
    windowless harness)."""

    @classmethod
    def setUpClass(cls):
        import prdata as P
        if not os.path.exists(P.VANILLA_ROM):
            raise unittest.SkipTest("no vanilla ROM")
        d = open(P.VANILLA_ROM, "rb").read()
        cls.rom = d[0x200:] if len(d) % 0x8000 == 0x200 else d
        cls.src = open(os.path.join(PR, "plugin", "WcPlugin", "WcStatusClock.cs"), encoding="utf-8").read()
        cls.hooks = open(os.path.join(PR, "plugin", "WcPlugin", "WcStatusTicks.cs"), encoding="utf-8").read()

    def code(self, addr, n):
        return self.rom[addr - 0xC00000:addr - 0xC00000 + n].hex()

    def test_battle_time_every_two_frames(self):
        # LDA $3A6C / LDX #$02 / CMP $0E / BEQ out / INC A / DEX / BNE: not the frame of the last move or the next
        self.assertEqual(self.code(0xC21129, 14), "ad6c3aa202c50ef05e1acad0f8ee")
        self.assertIn("TickSeconds = 2 / FramesPerSecond", self.src)

    def test_sixteen_slots_ten_fighters(self):
        # LDA $3A91 / INC $3A91 / AND #$0F / CMP #$0A: slots 10-15 are the battle's own timers
        self.assertEqual(self.code(0xC25A83, 10), "ad913aee913a290fc90a")
        self.assertIn("Slots = 16, Fighters = 10, PartySlots = 4", self.src)

    def test_counter_adds(self):
        # LDY #$20 / LDA $3EF8,X / BIT #$04 (Slow) / BNE / LDY #$40 / BIT #$08 (Haste) / BEQ / LDY #$54 / TYA / STA $3ADD,X
        self.assertEqual(self.code(0xC209D2, 19), "08a020bdf83e8904d008a0408908f002a05498")
        self.assertIn("AddNormal = 64, AddHaste = 84, AddSlow = 32", self.src)
        self.assertIn("Has(SnesStatus.Slow) ? AddSlow : Has(SnesStatus.Haste) ? AddHaste : AddNormal", self.src, "Slow is checked first")

    def test_step_table(self):
        handlers = {0x5B45: "R", 0x5B3B: "P", 0x5AE8: "-"}
        # $5B3B / $5B45: TYX / LDA $3E4C,X / ORA #$10 (Poison) or #$08 (Regen/Sap) / STA / RTS
        self.assertEqual(self.code(0xC25B3B, 20), "bbbd4c3e09109d4c3e60bbbd4c3e09089d4c3e60")
        table = "".join(handlers[int(self.code(0xC25AEA + 2 * i, 2)[2:] + self.code(0xC25AEA + 2 * i, 2)[:2], 16)]
                        for i in range(8))
        self.assertEqual(table, "R-P-R---")
        self.assertIn(f'StepTable = "{table}"', self.src)

    def test_random_start_and_stop(self):
        # LDX #$12 / JSR $4B5A (random byte) / STA $3AF0,X
        self.assertEqual(self.code(0xC22457, 8), "a212205a4b9df03a")
        self.assertIn("new SnesStatusClock(rng.Next(256))", self.hooks)
        # LDA $3AF1,X / BEQ / DEC $3AF1,X / BNE out: a stopped fighter's step does nothing else
        self.assertEqual(self.code(0xC25AA3, 14), "bdf13af009def13ad03ca9018055")
        self.assertIn("if (StopLeft > 0)", self.src)
        self.assertIn("if (Has(SnesStatus.Stop)) return true;", self.src)

    def test_intervals(self):
        fps = 60.0988
        visit = 16 * 2                          # frames between a fighter's visits
        for add, regen, poison in ((64, 512, 1024), (32, 1024, 2048)):
            self.assertEqual(4 * 256 // add * visit, regen)
            self.assertEqual(8 * 256 // add * visit, poison)
        self.assertAlmostEqual(512 / fps, 8.519, 3)
        self.assertAlmostEqual(4 * 256 / 84 * visit / fps, 6.491, 3)     # Haste: 12-13 visits a step's worth

    def test_timer_start_values(self):
        # C2/46B0: the on-set handlers, from Sleep (bit 15 of the 32) on: Sleep $4634, Stop $467D, Reflect $4687, Freeze $4691
        self.assertEqual(self.code(0xC246CE, 22), "34469b46634669466e467d469b469b4687469b469146")
        # PHP / SEP #$20 / LDA #n / STA timer,Y: Sleep 18 ($3CF9), Stop 18 ($3AF1), Reflect 26 ($3F0C), Freeze 34 ($3F0D)
        self.assertEqual(self.code(0xC24634, 8), "08e220a91299f93c")
        self.assertEqual(self.code(0xC2467D, 8), "08e220a91299f13a")
        self.assertEqual(self.code(0xC24687, 8), "08e220a91a990c3f")
        self.assertEqual(self.code(0xC24691, 8), "08e220a922990d3f")
        self.assertIn("SleepSteps = 18, StopSteps = 18, ReflectSteps = 26, FreezeSteps = 34", self.src)
        self.assertIn("var gained = seen ? s & ~had : SnesStatus.None;", self.src, "statuses from the start: no timer")

    def test_timers_count_on_steps(self):
        # C2/5B06: Reflect, Freeze, Sleep each DEC'd a step; at 0 the status's removal bit (02/04/08)
        self.assertEqual(self.code(0xC25B06, 46), "85b8bd0c3ff009de0c3fd004a90204b8bd0d3ff009de0d3fd004a90404b8"
                                                  "bdf93cf009def93cd004a90804b8a5b8")
        self.assertIn("void CountTimers()", self.src)
        # C2/5AB8: LDA $3B05 / CMP #2 / BCC / DEC / STA / DEC / BNE / JSR $5BC7 (death): Condemned ends as it reaches 1
        self.assertEqual(self.code(0xC25AB8, 17), "bd053bc902900a3a9d053b3ad00320c75b")
        self.assertIn("if (DoomLeft > 0) DoomLeft--;", self.src)
        for hook in ("GetConditionDurationTime", "InterruptRemoveCondition", "CheckForceMissCondition", "remainingDoomCount"):
            self.assertIn(hook, self.hooks)

    def test_condemned_count(self):
        # C2/463F flags the target; C2/09B4: LDA $11AF (the attacker's level) / JSR $4B65 (random 0..L-1) /
        # ADC $11AF / STA $EE / SEC / LDA #60 / SBC $EE / BCS +1 / TDC / ADC #20 / STA $3B05
        self.assertEqual(self.code(0xC2463F, 3), "a92000")
        self.assertEqual(self.code(0xC209B4, 26), "adaf1120654b186daf1185ee38a93ce5eeb0017b69149d053b60")
        self.assertIn("return ee <= 60 ? 81 - ee : 20;", self.src)
        self.assertIn("CondemnedCount(DoomLevel, rng) - 1", self.src, "the countdown: $3B05 - 1 steps")

    def test_jump_air(self):
        # C2/0B7A: STZ $3AB5 / LDA #$E0 / STA $322C: the jumper lands when its wait timer reaches 224 x 256
        self.assertEqual(self.code(0xC20B7A, 8), "9eb53aa9e09d2c32")
        # C2/1193: REP #$20 / LDA $3AC8 (the ATB's add) / LSR / CLC / ADC $3AB4 / STA $3AB4: half the ATB's add a move
        self.assertEqual(self.code(0xC21193, 13), "c220bdc83a4a187db43a9db43a")
        atb = open(os.path.join(PR, "plugin", "WcPlugin", "WcAtb.cs"), encoding="utf-8").read()
        self.assertIn("JumpWait = 224 * 256", atb)
        self.assertIn("int perTick = Math.Max(1, (s * k >> 4) >> 1);", atb)
        self.assertIn("int moves = (wait * 256 + perTick - 1) / perTick;", atb)
        # the PR's landing: ability 22 (type 16, "ContinuousJumpEnd", ability_wait 30) after Jump (21, type 15):
        # its chant time (BattleProgressATBFF6.GetChantingTime) is what the plugin sets to the SNES's air time
        import csv
        rows = {r["id"]: r for r in csv.DictReader(open(os.path.join(PR, "vanilla", "master", "Assets", "GameAssets", "Serial",
                                                                      "Data", "Master", "ability.csv"), encoding="utf-8"))}
        self.assertEqual((rows["21"]["type_id"], rows["21"]["ability_wait"]), ("15", "1"))
        self.assertEqual((rows["22"]["type_id"], rows["22"]["ability_wait"]), ("16", "30"))
        self.assertIn('"GetChantingTime"', atb)
        self.assertIn("if (ab.TypeId == 16)", atb)
        # Speed 33: (33 + 20) x 96 / 16 = 318 a move, 159 to the wait timer: 361 moves, 722 frames (1.75 gauges)
        per = (33 + 20) * 96 >> 4
        self.assertEqual(per, 318)
        self.assertEqual(-(-224 * 256 // (per >> 1)) * 2, 722)

    def test_command_waits(self):
        # C2/067B: a wait per SNES command (Fight 16, Magic 32, Morph/Revert 0, Jump 224 = its landing)
        import port
        table = bytes.fromhex(self.code(0xC2067B, 30))
        self.assertEqual((table[0], table[2], table[3], table[22]), (16, 32, 0, 224))
        atb = open(os.path.join(PR, "plugin", "WcPlugin", "WcAtb.cs"), encoding="utf-8").read()
        block = atb[atb.index("SnesCommandWait = new"):]
        block = block[:block.index("};")]
        plugin = {int(k): int(v) for k, v in re.findall(r"\[(\d+)\] = (\d+)", block)}
        want = {}
        for snes, pr in port.SNES_CMD.items():
            if snes == 22:          # Jump: up at once, the landing is WcAtb's own (the 224)
                continue
            want.setdefault(pr, table[snes])
        want[8] = table[3]          # the PR's Trance (8): Morph's 0, as its Revert (9)
        self.assertEqual(plugin, want, "the plugin's PR command -> wait = the ROM's table through SNES_CMD")
        # C2/1193's wait timer at half the ATB's add: Fight at Speed 33 ready after 26 moves, 52 frames
        per = ((33 + 20) * 96 >> 4) >> 1
        self.assertEqual(-(-16 * 256 // per) * 2, 52)

    @unittest.skipUnless(os.environ.get("FF6_SNES_TESTS") == "1", "set FF6_SNES_TESTS=1 to run the SNES (windowless harness)")
    def test_time_runs_through_an_attack_on_the_snes(self):
        # the battle-time counter ($3A3E) goes on while a Fight plays (as the PR's does: WcTest turn_cycle)
        import snes_status_ticks as T
        turns = T.run_turn()["turns"]
        self.assertTrue(len(turns) >= 2)
        for t in turns:
            moves, frames = t["done"][1] - t["ready"][1], t["done"][0] - t["ready"][0]
            self.assertTrue(30 <= moves <= 45 and moves * 2 <= frames <= moves * 2.6, f"attack: {moves} moves over {frames} frames")

    @unittest.skipUnless(os.environ.get("FF6_SNES_TESTS") == "1", "set FF6_SNES_TESTS=1 to run the SNES (windowless harness)")
    def test_waits_measured_on_the_snes(self):
        import snes_status_ticks as T
        for cmd, wait in ((0x00, 0x10), (0x05, 0x10), (0x02, 0x20)):
            r = T.run_wait(cmd)
            per = r["atb"] >> 1
            rule = -(-wait * 256 // per) * 2
            turns = [(w, b - a) for w, a, b in r["turns"]]
            self.assertTrue(turns and all(w == wait and rule - 2 <= d <= rule + 40 for w, d in turns),
                            f"command {cmd:02x}: {turns} (wait ${wait:02X}: {rule} frames by the rule)")

    @unittest.skipUnless(os.environ.get("FF6_SNES_TESTS") == "1", "set FF6_SNES_TESTS=1 to run the SNES (windowless harness)")
    def test_timers_measured_on_the_snes(self):
        import snes_status_ticks as T
        t = {e[2]: e[0] for e in T.run_timers("timers")["events"]}
        for name, steps in (("Sleep off", 18), ("Reflect off", 26), ("Freeze off", 34)):
            self.assertTrue((steps - 1) * 128 <= t[name] <= steps * 131, f"{name}: {t[name]} frames, {steps} steps")
        dead = T.run_timers("condemned")["events"]
        self.assertTrue(dead and 28 * 128 <= dead[0][0] <= 29 * 131, f"Condemned 30: {dead}")
        j = T.run_timers("jump-air", 6000)
        ups = [e[0] for e in j["events"] if e[2] == "up"]
        downs = [e[0] for e in j["events"] if e[2] == "down"]
        air = [d - u for u, d in zip(ups, downs)]
        rule = -(-224 * 256 // ((j["atb"]) >> 1)) * 2
        self.assertTrue(air and all(rule <= a <= rule + 30 for a in air), f"Jump air {air}, rule {rule}")

    @unittest.skipUnless(os.environ.get("FF6_SNES_TESTS") == "1", "set FF6_SNES_TESTS=1 to run the SNES (windowless harness)")
    def test_measured_on_the_snes(self):
        import snes_status_ticks as T
        r = T.run("cadence")
        by = {}
        for f, slot, what, amount in r["ticks"]:
            by.setdefault(what, []).append(f)
        for what, every in (("regen", 512), ("sap", 512), ("poison", 1024)):
            fr = by[what][1:] if what == "poison" else by[what]      # Poison's first change: a monster's hit
            gaps = [b - a for a, b in zip(fr, fr[1:])]
            self.assertTrue(gaps and all(every <= g <= every * 1.02 for g in gaps), f"{what}: {gaps}")
        jump = T.run("jump-poison", 9000)
        air = jump["air"]
        landed_in_air = [t for t in jump["ticks"] if t[3] < 0 and any(
            a[1] and a[0] <= t[0] < (air[i + 1][0] if i + 1 < len(air) else 10 ** 9) for i, a in enumerate(air))]
        self.assertTrue(landed_in_air, f"Poison lands while jumping: {jump['ticks']} {air}")
        stop = T.run("stop", 9000)
        on, off = stop["stop"][0][0], stop["stop"][1][0]
        self.assertFalse([t for t in stop["ticks"] if on <= t[0] < off and t[3] > 0], "no Regen while stopped")


class WcEncounterRates(unittest.TestCase):
    """WcEncounters' rate tables from the vanilla ROM (WC keeps vanilla's rates): other maps' 2-bit
    codes (0xF5880) through each map's pack (0xF5600) to the PR monster set the porter matches to it;
    World of Balance zones (0xF5800, a zone's 4 terrain groups at bits 7-6 .. 1-0: the field code's
    BattleBGRateTbl shifts 3, 2, 1, 0); and the SNES's mean steps per battle the in-game check uses."""

    @classmethod
    def setUpClass(cls):
        import prdata as P
        import ff6snes as SN
        import port_encounters as PE
        cls.rom = SN.Rom(P.VANILLA_ROM)
        cls.s2p = PE.EncounterPorter(cls.rom, cls.rom, {}, lambda *a: None).set_to_pack()
        cls.src = open(os.path.join(PR, "plugin", "WcPlugin", "WcEncounters.cs"), encoding="utf-8").read()

    def table(self, name):
        block = self.src[self.src.index(name):]
        block = block[:block.index("};")]
        return {int(k): int(v) for k, v in re.findall(r"\[(\d+)\] = (\d)", block)}

    def test_other_maps(self):
        d = self.rom.data
        want = {}
        for m in range(415):
            code = (d[0xF5880 + m // 4] >> (m % 4 * 2)) & 3
            if d[0x2D8F00 + 33 * m + 5] & 0x80 and code:
                pack = d[0x0F5600 + m]
                users = [x for x in range(415) if d[0x2D8F00 + 33 * x + 5] & 0x80 and d[0x0F5600 + x] == pack]
                self.assertEqual(users, [m], f"pack {pack} belongs to SNES map {m} alone")
                sets = [s for s, p in self.s2p.items() if p == pack]
                self.assertTrue(sets, f"a PR set for pack {pack}")
                for s in sets:
                    want[s] = code
        self.assertEqual(self.table("SetCode"), want)

    def test_world_zones(self):
        d = self.rom.data
        want, forest = {}, None
        for z in range(64):
            codes = [(d[0xF5800 + z] >> (2 * (3 - x))) & 3 for x in range(4)]
            self.assertEqual(d[0xF5840 + z], 0, "the World of Ruin is all normal")
            if not any(codes):
                continue
            common = max(set(codes), key=codes.count)
            want[z] = common
            for x, c in enumerate(codes):
                if c != common:
                    pack = d[0xF5400 + 4 * z + x]
                    sets = [s for s, p in self.s2p.items() if p == pack and 401 <= s <= 425]
                    self.assertEqual((z, c, len(sets)), (10, 0, 1), "zone 10's forest is the one exception")
                    forest = sets[0]
        self.assertEqual(self.table("WobZoneCode"), want)
        self.assertIn(f"Zone10Forest = {forest};", self.src)

    def test_means_in_the_check(self):
        def mean(r):
            alive, dg, m, n = 1.0, 0, 0.0, 0
            while alive > 1e-12:
                n += 1
                dg = min(dg + r, 0xFF00)
                p = (dg >> 8) / 256.0
                m += n * alive * p
                alive *= 1 - p
            return round(m, 2)
        test = open(os.path.join(PR, "plugin", "WcPlugin", "WcTest.cs"), encoding="utf-8").read()
        for r in (0xC0, 0x60, 0x180, 0x70, 0x40, 0x160, 0x200):
            self.assertIn(f"[0x{r:X}] = {mean(r):g}", test)


if __name__ == "__main__":
    unittest.main()
