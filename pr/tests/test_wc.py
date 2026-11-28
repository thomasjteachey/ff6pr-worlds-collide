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
import copy
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

def wc_test_source():
    """WcTest.cs plus the checks split out of it into WcTestChecks/ (2026-11-21)."""
    import glob as _glob
    d = os.path.join(PR, "plugin", "WcPlugin")
    parts = [os.path.join(d, "WcTest.cs")] + sorted(_glob.glob(os.path.join(d, "WcTestChecks", "*.cs")))
    return chr(10).join(open(f, encoding="utf-8").read() for f in parts)

sys.path.insert(0, os.path.join(PR, "tools"))
import kitpath  # noqa: E402,F401  (the modding kit's tools, when it's this repository's kit/ submodule)
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
        cls.assets = {}
        for p in glob.glob(os.path.join(cls.tmp, "**", "*.json"), recursive=True):
            name = os.path.basename(p)[:-5].lower()
            cls.assets[name] = p
            if "Mnemonics" in load(p):
                cls.built[name] = p

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

    # --- Search the skies (WC event/airship.py doom_gaze_mod) ---------------------------------
    def in_the_wor(self):
        st, _ = self.new_game()
        self.wheel(st, "WC_HELM_WOR")
        st.set(*FALCON_RELEASED, 1)
        return st

    def falcon_wheel(self, st, answer, gate_open):
        return Run(self.script("sc_e_FAoutboard"), st, [answer], {"WC:gate doom_gaze": gate(gate_open)})

    @unittest.skipUnless(E.SEARCH_THE_SKIES, "Search the skies is off")
    def test_falcon_wheel_offers_search_the_skies(self):
        """WC's WoR wheel: Lift-off / World of Balance / Search The Skies / Not just yet while Doom
        Gaze's gate (Setzer, -cg) is met and he isn't beaten; without the gate, or once his check
        is done, only the first two (and never mind)."""
        st = self.in_the_wor()
        r = self.falcon_wheel(st, OPT_NEVER_MIND, True)
        self.assertEqual(r.offered, [[OPT_WHEEL, "WC_HELM_WOB", "WC_HELM_SKIES", OPT_NEVER_MIND]])
        self.assertEqual(r.end, ("Exit",))
        self.assertEqual(st.get(E.WC_FLAGS, E.SKIES_PENDING), 0)
        closed = self.falcon_wheel(st, OPT_NEVER_MIND, False)
        self.assertEqual(closed.offered, [[OPT_WHEEL, "WC_HELM_WOB", OPT_NEVER_MIND]], "gate closed")
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["doom_gaze"], 1)
        done = self.falcon_wheel(st, OPT_NEVER_MIND, True)
        self.assertEqual(done.offered, [[OPT_WHEEL, "WC_HELM_WOB", OPT_NEVER_MIND]], "Doom Gaze beaten")
        self.assertEqual(done.did("SysCall", "WC:gate doom_gaze"), [], "no gate asked once the check is done")
        vanilla = Run(self.script("sc_e_FAoutboard"), State(), [OPT_NEVER_MIND])
        self.assertEqual(vanilla.offered, [[OPT_WHEEL, OPT_NEVER_MIND]], "not a WC game")
        self.assertEqual(E.MESSAGES["WC_HELM_SKIES"], "(Search the skies.)")
        self.assertEqual(E.TRACK_NAMES["doom_gaze"], "Search The Skies")

    @unittest.skipUnless(E.SEARCH_THE_SKIES, "Search the skies is off")
    def test_search_the_skies_fights_doom_gaze_on_the_deck(self):
        """Picked: the PR's Doom Gaze fight (sc_e_0964's EncountBoss) on the deck, as WC fights him
        (the sky's start lost the game, wc-11/wc-12), with the wheel's own script as its follow-up
        (a follow-up loads from the battle's map package: the deck's has it and sc_e_0964_2, not
        sc_e_0964_1). It does sc_e_0964_1's work: beaten, the reward scene; escaped, the deck."""
        st = self.in_the_wor()
        r = self.falcon_wheel(st, "WC_HELM_SKIES", True)
        fights = r.did("EncountBoss")
        self.assertEqual([(t[2][0], t[3][0]) for t in fights], [(E.DOOM_GAZE_PARTY, "sc_e_FAoutboard")])
        self.assertEqual(r.end, ("Exit",))
        self.assertEqual(st.get(E.WC_FLAGS, E.SKIES_PENDING), 1)
        self.assertEqual(r.did("ChangeMap"), [], "no take-off")
        self.assertEqual([t for t in r.did("SysCall") if t[3][0] in ("WC:to_wob", "WC:to_wor")], [], "no world switch")
        sky = S.load("sc_e_0964")["Mnemonics"][1]
        self.assertEqual(fights[0][2][:1], sky["operands"]["iValues"][:1])
        # escaped: his HP left (ScenarioVariable 3), back on the deck, the choice stays
        st.set("ScenarioVariable", 3, 1000)
        esc = Run(self.script("sc_e_FAoutboard"), st)
        self.assertEqual((esc.end, len(esc.did("FadeIn"))), (("Exit",), 1))
        self.assertEqual((st.get(E.WC_FLAGS, E.SKIES_PENDING), esc.did("Select")), (0, []))
        self.assertEqual(self.falcon_wheel(st, OPT_NEVER_MIND, True).offered,
                         [[OPT_WHEEL, "WC_HELM_WOB", "WC_HELM_SKIES", OPT_NEVER_MIND]])
        # beaten: the reward scene
        self.falcon_wheel(st, "WC_HELM_SKIES", True)
        st.set("ScenarioVariable", 3, 0)
        won = Run(self.script("sc_e_FAoutboard"), st)
        self.assertEqual(won.end[:2] + won.end[-1:], ("ChangeMap", 268, "sc_e_0964_2"), won.end)
        self.assertEqual(st.get(E.WC_FLAGS, E.SKIES_PENDING), 0)
        self.assertEqual(Run(self.script("sc_e_FAoutboard"), st, [OPT_NEVER_MIND]).did("EncountBoss"), [],
                         "talking to the wheel again: the plain wheel")
        sa = S.load("sc_e_FAoutboard_1")
        self.assertEqual(Run(sa, st, start=next(n for n, m in enumerate(sa["Mnemonics"])
                                                if m["mnemonic"] == "ChangeScript") + 1).did("EncountBoss"), [],
                         "take-offs are plain ones")

    @unittest.skipUnless(E.SEARCH_THE_SKIES, "Search the skies is off")
    def test_search_the_skies_reward_hides_the_choice(self):
        """Winning: the sky fight's own reward scene (sc_e_0964_2) gives the seed's Doom Gaze reward
        and sets its check flag (as the random encounter does), then flies on; the wheel no longer
        offers the search."""
        st = self.in_the_wor()
        self.falcon_wheel(st, "WC_HELM_SKIES", True)
        st.set("ScenarioVariable", 3, 0)
        Run(self.script("sc_e_FAoutboard"), st)          # the fight's follow-up: beaten
        rewarded = []
        scene = Run(self.script("sc_e_0964_2"), st, syscalls={"WC:reward doom_gaze 0": lambda s: rewarded.append(1)})
        self.assertEqual(rewarded, [1])
        self.assertEqual(st.get(E.WC_FLAGS, E.CHECK_FLAGS["doom_gaze"]), 1)
        self.assertEqual(scene.end[:2], ("ChangeMap", E.WOR_MAP), scene.end)
        r = self.falcon_wheel(st, OPT_NEVER_MIND, True)
        self.assertEqual(r.offered, [[OPT_WHEEL, "WC_HELM_WOB", OPT_NEVER_MIND]])

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

    def test_wrexsoul_no_zinger_releases_battle_control(self):
        """PR's forced Fury must not run when WC's -wnz removed that opening Zinger."""
        built, _ = self.battle_script("sc_b_0939")
        # End immediately where the real controller starts AI.  This isolates the opening from its
        # intentionally endless battle loop while retaining calls into vanilla Event00.
        sc = copy.deepcopy(built)
        start_ai = next(i for i, m in enumerate(sc["Mnemonics"]) if m["mnemonic"] == "ExecBattleAI")
        sc["Mnemonics"][start_ai] = S.op("Exit")

        st = State()
        st.set(E.WC_FLAGS, E.WC_GAME, 1)
        wc = Run(sc, st, syscalls={"WC:has -wnz": gate(True)})
        self.assertEqual([t[2][0] for t in wc.did("BattleInteractive")], [0, 1])
        self.assertEqual([t[2][0] for t in wc.did("BattlePause")], [1, 0])
        self.assertEqual(wc.did("Msg"), [], "no removed opening dialogue")
        self.assertEqual(wc.did("SysCall", "敵バトル行動：ヒューリー"), [], "no removed Fury/Zinger")
        self.assertEqual(wc.did("SysCall", "ソウルセイバー座標保存"), [], "no possession coordinate state")

        vanilla = Run(sc, State())
        self.assertEqual([t[3][0] for t in vanilla.did("Msg")], ["E0939_01_537_b_01"])
        self.assertTrue(vanilla.did("SysCall", "敵バトル行動：ヒューリー"), "vanilla keeps its opening")

        # Isolate one pass through the main loop: -wnz skips the possession-only Soul Saver check
        # and reaches synchronization; vanilla still calls it.
        loop = copy.deepcopy(built)
        sync = next(i for i, m in enumerate(loop["Mnemonics"]) if m["mnemonic"] == "SyncBattleAI")
        loop["Mnemonics"][sync] = S.op("Exit")
        hook = next(i for i, m in enumerate(loop["Mnemonics"]) if m.get("label") == "wc_no_zinger_soul_saver")
        st = State()
        st.set(E.WC_FLAGS, E.WC_GAME, 1)
        wc_loop = Run(loop, st, start=hook, syscalls={"WC:has -wnz": gate(True)})
        self.assertEqual(wc_loop.did("SysCall", "ソウルセイバーチェック"), [])
        vanilla_loop = Run(loop, State(), start=hook)
        self.assertTrue(vanilla_loop.did("SysCall", "ソウルセイバーチェック"))

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

    def test_opera_entrance_is_open_in_wc(self):
        st, _ = self.new_game()
        self.assertFalse(self.sets("sc_map_20160", st),
                         "SNES WC removes the entrance attendant instead of spawning ev_e_0278")

        vanilla = State()
        vanilla.set("ScenarioFlag1", 158, 1)
        self.assertEqual(self.sets("sc_map_20160", vanilla), ["ev_e_0278"],
                         "the unmodified PR story still has its performance attendant")

    def test_opera_disruption_still_starts(self):
        st, _ = self.new_game()
        st.set("ScenarioFlag1", 165, 1)
        r = Run(self.script("sc_npc_20161_2_1"), st)
        self.assertEqual(r.end, ("ChangeScript", "sc_e_0330"),
                         "the original impresario advances only after the performance")

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

    # --- WC's start state, the start-bits audit of 2026-11-21 -----------------------------------
    def test_wob_thamasa_has_leos_grave_and_its_townsfolk(self):
        # WC event/burning_house.py sets DEFEATED_KEFKA_THAMASA, LEO_BURIED_THAMASA,
        # FINISHED_THAMASA_KEFKA and THAMASA_CITIZENS: Leo's grave and sword with the three
        # townsfolk (ev_e_0552_2 + reo_sword), not the blocker before it (ev_e_0552_1), not the
        # townsfolk after Zozo (ev_e_0413: its man and boy are ev_e_0552_2's). While the burning
        # house is open its crowd (ev_e_0523) is there too: the grave layer's man and boy hidden
        sc = self.script("sc_map_20270")
        for done in (1, 0):
            for zozo in (0, 1):
                st, _ = self.new_game()
                st.set(E.WC_FLAGS, E.CHECK_FLAGS["burning_house"], done)
                st.set("ScenarioFlag1", 203, zozo)
                r = Run(sc, st)
                layers = [t[3][0] for t in r.did("SetEntities")]
                self.assertIn("ev_e_0552_2", layers, (done, zozo))
                self.assertFalse({"ev_e_0552_1", "ev_e_0413"} & set(layers), (done, zozo, layers))
                self.assertEqual("ev_e_0523" in layers, not done, "the burning house's door while its check is open")
                self.assertIn(167, [t[2][0] for t in r.did("Call")], "reo_sword: the grave and sword shown")
                hidden = sorted(t[2][1] for t in r.did("SetPuppet") if t[2][1] in E.GRAVE_TOWNSFOLK)
                self.assertEqual(hidden, [] if done else sorted(E.GRAVE_TOWNSFOLK), (done, zozo))
        st, _ = self.new_game()
        self.assertEqual((st.get("ScenarioFlag1", 203), st.get("ScenarioFlag2", 26)), (0, 0),
                         "neither flag is a start flag (203 arms Blackjack/Narshe scenes, 26 the Blackjack's breakdown)")
        # the hidden ids: the grave layer's man and boy, the burning-house crowd's (the same scripts)
        town = os.path.dirname(S.path("sc_map_20270"))

        def scripts(layer, ids):
            props = [{p["name"]: p["value"] for p in o["properties"]}
                     for g in load(os.path.join(town, layer + ".json"))["layers"] for o in g["objects"]]
            return sorted(p["script_id"] for p in props if p.get("entity_id") in ids)
        self.assertEqual(scripts("ev_e_0552_2", E.GRAVE_TOWNSFOLK), [6105, 6106])
        self.assertEqual(scripts("ev_e_0523", (149, 151)), [6105, 6106])
        vanilla = [t[3][0] for t in Run(sc, State()).did("SetEntities")]
        self.assertIn("ev_e_0552_1", vanilla, "vanilla before Leo's burial: the path blocked")
        self.assertNotIn("ev_e_0552_2", vanilla)

    def test_thamasa_inn_charges_wcs_price(self):
        # WC's start sets MET_STRAGO_RELM: the SNES innkeeper (CB/D73F) asks 1 gil, not 1500; with
        # -cg until Strago is recruited, WC's stranger price (100000000 GP): not enough money
        import prdata as P
        price = {int(r["group_id"]): int(r["coefficient"]) for r in P.Table("product") if r["content_id"] == "0"}
        self.assertEqual((price[114], price[115], price[127]), (1500, 1, 1), "the stranger's, the after-fire and WoR inns")
        st, _ = self.new_game()
        self.assertEqual(self.sets("sc_map_20271_4", st), ["ev_e_0540"], "the after-fire innkeeper (sc_sty_20271_4)")
        self.assertEqual(self.sets("sc_map_20271_4", State()), ["ev_e_0413"], "vanilla: the stranger's (1500)")
        for name, group in E.THAMASA_INNS:
            sc = self.script(name)
            for open_, answer in ((True, 0), (False, 0), (False, 1)):
                st, _ = self.new_game()
                r = Run(sc, st, answers=[answer], syscalls={"WC:gate burning_house": gate(open_)})
                lines = [t[3][0] for t in r.did("Msg")]
                if open_:
                    self.assertEqual([t[2][0] for t in r.did("SetGilTag")], [group], name)
                    self.assertEqual([t[2][0] for t in r.did("RemoveGil")], [group], f"{name}: 1 gil")
                    self.assertNotIn("WC_THAMASA_INN", lines)
                else:
                    self.assertEqual(r.offered, [list(E.THAMASA_STRANGER_CHOICES)], name)
                    self.assertEqual(lines, ["WC_THAMASA_INN"] + ([E.NOT_ENOUGH_GIL] if answer == 0 else []), (name, answer))
                    self.assertEqual((r.did("RemoveGil"), r.end), ([], ("Exit",)), name)
            st = State()                                     # not a WC game: the vanilla inn
            r = Run(sc, st, answers=[1])
            self.assertEqual([t[2][0] for t in r.did("SetGilTag")], [group])
            self.assertFalse(r.did("SysCall"))
        self.assertIn("100,000,000 gil", E.MESSAGES["WC_THAMASA_INN"])

    def test_mt_zozo_has_no_book_of_secrets(self):
        # WC event/mt_zozo.py: BOOK_OF_SECRETS_CHEST_MT_ZOZO cleared, the chest and its key NOP'd
        st, _ = self.new_game()
        st.set("ScenarioFlag2", 109, 1)                      # the mt_zozo check sets it
        self.assertEqual(self.sets("sc_map_30690", st), [], "no sparkling key on the cliff")
        for key in (0, 1):                                   # 1: a save that took the key before
            st.set("ScenarioFlag2", 110, key)
            r = Run(self.script("sc_e_0737"), st)
            self.assertEqual([t[3][0] for t in r.did("Msg")], ["E0737_00_474_a_01"], "It's locked.")
            self.assertEqual((r.did("GetItem"), r.did("TreasureBox"), st.get("ScenarioFlag2", 111)), ([], [], 0))
        vanilla = State()
        vanilla.set("ScenarioFlag2", 109, 1)
        self.assertEqual(self.sets("sc_map_30690", vanilla), ["ev_e_0736"])
        vanilla.set("ScenarioFlag2", 110, 1)
        self.assertEqual([t[2][:2] for t in Run(self.script("sc_e_0737"), vanilla).did("GetItem")], [[48, 1]], "vanilla's Books")

    def test_doma_family_not_lying_in_their_room(self):
        # WC event/doma_wob.py clears VARIOUS_DOMA_POISONING: SNES map 124's wife and son hidden
        st, _ = self.new_game()
        r = Run(self.script("sc_map_20101_6"), st)
        self.assertEqual((r.did("SetEntities"), r.did("Call")), ([], []), "no Mina and Shun, no Fall cue")
        self.assertEqual(self.sets("sc_map_20101_6", State()), ["ev_e_0120"], "vanilla is unchanged")

    def test_phantom_forest_ghost_merchant_after_the_train(self):
        # WC event/phantom_train.py ghost_shop_forest_mod: after the train a ghost in the forest
        # runs the train's shop (CB/AD44). Here an NPC in the spring room's entity_default (a game
        # file with our change, as a player's setup makes it), hidden on arrival unless it's due
        def hidden(st):
            r = Run(self.script("sc_map_30421_2"), st)
            return sorted(t[2][1] for t in r.did("SetPuppet")), r
        for done in (0, 1):
            st, _ = self.new_game()
            st.set(E.WC_FLAGS, E.CHECK_FLAGS["phantom_train"], done)
            puppets, r = hidden(st)
            self.assertEqual(puppets, [] if done else [E.GHOST_SHOP_ENTITY], done)
            self.assertEqual((r.did("SetEntities"), r.end), ([], ("Exit",)), "no walk to the spring either way")
        puppets, r = hidden(State())
        self.assertEqual((puppets, [t[3][0] for t in r.did("SetEntities")]), ([E.GHOST_SHOP_ENTITY], ["ev_e_0158"]),
                         "another game: never the merchant, vanilla's walk")
        room = os.path.dirname(S.path("sc_map_30421_2"))
        built = os.path.join(self.tmp, *E.ghost_shop_rel("entity_default.json").split("/"))
        doc, van = load(built), load(os.path.join(room, "entity_default.json"))
        objs = [o for g in doc["layers"] for o in g.get("objects") or []]
        old = [o for g in van["layers"] for o in g.get("objects") or []]
        self.assertEqual([o for o in objs if o["id"] < 1000], old, "the room's own objects as they were")
        props = {o["id"]: {p["name"]: p["value"] for p in o["properties"]} for o in objs if o["id"] >= 1000}
        (o, p), (ao, ap) = [(next(x for x in objs if x["id"] == k), props[k]) for k in (1000, 1001)]
        import prdata as P
        self.assertEqual(P.Table("script").rows[p["script_id"]]["script_name"], E.GHOST_SHOP_SCRIPT)
        self.assertEqual((p["object_type"], p["entity_id"], p["action_id"], p["asset_id"], p["move_type"]),
                         (6, E.GHOST_SHOP_ENTITY, 2, 139, 2))
        x, y = E.GHOST_SHOP_SPOT
        self.assertEqual((o["x"], o["y"]), (16 * x, 16 * (y + 1)), "WC's spot (map 0x85 (13, 10)), bottom-anchored")
        self.assertEqual((ap["object_type"], p["link_object_id"]), (13, ap["object_id"]), "it wanders its MoveArea")
        self.assertTrue(ao["x"] <= 16 * x < ao["x"] + ao["width"] and ao["y"] <= 16 * y < ao["y"] + ao["height"])
        coll = load(os.path.join(room, "collision.json"))["layers"][0]
        self.assertEqual(coll["data"][y * coll["width"] + x], 0, "a walkable tile")
        used = {q["value"] for f in glob.glob(os.path.join(room, "*.json")) if os.path.basename(f).startswith(("ev_", "entity_default"))
                for g in load(f)["layers"] for ob in g.get("objects") or [] for q in ob.get("properties", []) if q["name"] == "entity_id"}
        self.assertFalse(used & {p["entity_id"], ap["entity_id"]}, "entity ids of its own")
        # its script: the train's own (byte for byte), listed in the map's package
        built = os.path.join(self.tmp, *E.ghost_shop_rel(E.GHOST_SHOP_SCRIPT + ".json").split("/"))
        with open(built, "rb") as a, open(S.path(E.GHOST_SHOP_SCRIPT), "rb") as b:
            self.assertEqual(a.read(), b.read())
        shop = load(built)["Mnemonics"]
        self.assertEqual([m["operands"]["sValues"][0] for m in shop if m["mnemonic"] in ("Msg", "SysCall")],
                         ["E1049_00_360_a_01", "ショップを開く:魔列車"], "the train ghost's offer and shop")
        package = load(os.path.join(self.tmp, "map_30421", "Assets", "GameAssets", "Serial", "Res", "Map", "Map_30421", "package.json"))
        entry = next(m for m in package["map"] if m["name"] == "Map_30421_2")
        self.assertIn({"name": E.GHOST_SHOP_SCRIPT, "asset": f"Map_30421_2/{E.GHOST_SHOP_SCRIPT}"}, entry["script"])
        self.assertIn({"name": "ev_e_0158", "asset": "Map_30421_2/ev_e_0158"}, entry["entity"], "vanilla's lists kept")

    def test_no_walk_to_the_phantom_forest_spring(self):
        # WC event/phantom_train.py forest_spring_mod deletes the walk to the healing spring
        st, _ = self.new_game()
        self.assertEqual(st.get("ScenarioFlag1", 106), 0)
        self.assertNotIn("ev_e_0158", self.sets("sc_map_30421_2", st), "no auto-walk on the first visit")
        r = Run(self.script("sc_e_0159"), st)                # the spring itself, as ever
        self.assertEqual((len(r.did("RecoveryHP")), r.end), (1, ("Exit",)))
        self.assertEqual(self.sets("sc_map_30421_2", State()), ["ev_e_0158"], "vanilla walks there")

    def test_siegfried_blocks_the_wor_figaro_cave_while_its_gate_is_closed(self):
        # WC event/figaro_castle_wor.py with -cg: SIEGFRIED_FIGARO_CAVE_ENTRANCE set; he only says
        # "Pretty dangerous from here on. Wait here." until Edgar is recruited
        for open_ in (False, True):
            st, _ = self.new_game()
            r = Run(self.script("sc_map_30641_5"), st, syscalls={"WC:gate figaro_castle_wor": gate(open_)})
            self.assertEqual([t[3][0] for t in r.did("SetEntities")], [] if open_ else ["ev_e_0666"], open_)
            r = Run(self.script("sc_e_0666"), st)
            self.assertEqual([t[3][0] for t in r.did("Msg")], ["WC_SIEGFRIED_WAIT"])
            self.assertEqual((r.did("Call"), r.did("PlayBGM"), st.get("ScenarioFlag2", 75), r.end), ([], [], 1, ("Exit",)),
                             "he stays: no run-off, no music, his flag as WC's start set it")
        self.assertEqual(E.MESSAGES["WC_SIEGFRIED_WAIT"], "Pretty dangerous from here on. Wait here.")
        vanilla = State()
        vanilla.set("ScenarioFlag2", 74, 1)
        self.assertEqual(self.sets("sc_map_30641_5", vanilla), ["ev_e_0666"])
        Run(self.script("sc_e_0666"), vanilla)
        self.assertEqual(vanilla.get("ScenarioFlag2", 75), 1, "vanilla's scene runs off")

    def test_morph_from_the_start(self):
        # WC's start enables Morph (zozo.py: ENABLE_MORPH_COMMAND). The port deals SNES Morph as the
        # PR's command 9 (Terra's row, "Revert", listed only in Trance); Trance (8) is added only by
        # Zozo's scene with Maduin (sc_e_0413_2's SysCall), which a WC game never plays: the plugin
        # gives 8 to whoever holds 9 (WcEvents.GiveMorph, from WcObjectives.Poll)
        import port
        import prdata as P
        self.assertEqual((port.SNES_CMD[3], port.SNES_CMD[4]), (9, 9))
        terra = P.Table("character_status").rows[1]
        cmds = [int(terra[f"command_id{k}"]) for k in range(1, 7)]
        self.assertIn(9, cmds)
        self.assertNotIn(8, cmds)
        names = {int(r["id"]): r["mes_id_name"] for r in P.Table("command")}
        self.assertEqual((names[8], names[9]), ("MSG_CMD_NAME_04", "MSG_CMD_NAME_05"), "Trance, Revert")
        zozo = S.load("sc_e_0413_2")["Mnemonics"]
        self.assertIn("コマンド追加：トランス", [m["operands"]["sValues"][0] for m in zozo if m["mnemonic"] == "SysCall"])
        plugin = os.path.join(PR, "plugin", "WcPlugin")
        events = open(os.path.join(plugin, "WcEvents.cs"), encoding="utf-8").read()
        poll = open(os.path.join(plugin, "WcObjectives.cs"), encoding="utf-8").read()
        self.assertIn("TranceCommand = 8, MorphSlot = 9", events)
        self.assertIn("client.AddCommand(c.Id, TranceCommand)", events)
        self.assertIn("WcEvents.GiveMorph();", poll)

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
        wor = Run(self.script("sc_map_20381_1"), st, syscalls={"WC:gate kohlingen": gate(True)})
        self.assertEqual(len(wor.did("SetEntities", "ev_e_0681")), 1,
                         "Kohlingen's WoR inn has the SNES shared-map check too")
        self.assertEqual([t[2][0] for t in wor.did("PlayBGM")], [21])
        closed = Run(self.script("sc_map_20381_1"), st, syscalls={"WC:gate kohlingen": gate(False)})
        self.assertEqual(closed.did("SetEntities", "ev_e_0681"), [])
        layer = load(self.assets["ev_e_0681"])
        ids = {p["value"] for group in layer["layers"] for obj in group.get("objects", [])
               for p in obj.get("properties", []) if p["name"] == "entity_id"}
        self.assertEqual(ids, {1, 9}, "the WoR check actors avoid that map's occupied field ids 2-8")
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

    def test_auction_is_open_without_the_vanilla_cranes_story_flag(self):
        """WC removes the auction's post-Magitek-Factory requirement. A fresh WC state keeps
        ScenarioFlag1 203 clear, but must reach the auction greeting rather than its closed line;
        the map-local flag still closes a second auction during the same visit."""
        st, _ = self.new_game()
        self.assertEqual(st.get("ScenarioFlag1", 203), 0)
        sc = self.script("sc_e_0422")
        first = Run(sc, st, answers=[1])
        self.assertIn("E0422_99_111_a_01", [t[3][0] for t in first.did("Msg")])
        self.assertNotIn("N014_C00_111_01_01", [t[3][0] for t in first.did("Msg")])
        st.set("MapLocalVariable", 1, 1)
        second = Run(sc, st)
        self.assertEqual([t[3][0] for t in second.did("Msg")], ["N014_C00_111_01_01"])

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

    def test_lete_river_skips_the_first_ride_tutorial(self):
        # WC lete_river.py removes the entire first-ride setup.  With its story bit clear, an
        # arbitrary WC party enters the river script directly; both PR approach-map animations
        # use story puppets and can wait forever because WC never created that story party.
        st, _ = self.new_game()
        self.assertEqual(st.get("ScenarioFlag1", 48), 0)
        r = Run(self.script("sc_e_0063"), st,
                syscalls={"WC:gate lete_river": gate(True)})
        self.assertEqual(r.end, ("ChangeScript", "sc_e_0063_3"))
        self.assertEqual(r.did("Select"), [], "the WC entrance is the raft trigger itself")
        self.assertEqual(r.did("RecoveryHP"), [], "no vanilla first-ride healing")
        self.assertEqual(r.did("HideMob"), [], "no missing story-party mob")
        self.assertEqual(r.did("ColorFade"), [], "no first-ride tutorial transition")
        self.assertEqual(r.did("SetPuppet"), [], "no approach-map fall animation")
        self.assertEqual(st.get("ScenarioFlag1", 48), 0, "WC does not set the vanilla story bit")

        closed, _ = self.new_game()
        blocked = Run(self.script("sc_e_0063"), closed,
                      syscalls={"WC:gate lete_river": gate(False)})
        self.assertEqual(blocked.end, ("Exit",))
        self.assertEqual(blocked.did("ChangeScript"), [])

        # SNES WC's river exit loads the WoB with the airship on the party's landing tile.
        # PR's copied second-ride exit alone leaves the Blackjack wherever it was before the check.
        finished, _ = self.new_game()
        washed_up = Run(self.script("sc_e_0065_1"), finished,
                         syscalls={"WC:gate lete_river": gate(True)})
        vehicles = [t[2][:6] for t in washed_up.did("SetVehicle")]
        self.assertIn([E.BLACKJACK, E.WOB_MAP, *E.LETE_WASHUP, 1, 3], vehicles)
        self.assertEqual(finished.get("TreasureFlag4", 135), 1, "the Blackjack is boardable")
        self.assertEqual(washed_up.end, ("ChangeMap", E.WOB_MAP, 35, ""))

        # An already-completed save is repaired once when the WoB world map initializes.  It
        # must not keep teleporting the Blackjack after the player has flown it elsewhere.
        old_save, _ = self.new_game()
        old_save.set(E.WC_FLAGS, E.CHECK_FLAGS["lete_river"], 1)
        world = self.script("sc_map_10010")
        repaired = Run(world, old_save)
        self.assertIn([E.BLACKJACK, E.WOB_MAP, *E.LETE_WASHUP, 1, 3],
                      [t[2][:6] for t in repaired.did("SetVehicle")])
        self.assertEqual(old_save.get(E.WC_FLAGS, E.LETE_AIRSHIP_REPAIRED), 1)
        self.assertEqual(old_save.get("TreasureFlag4", 135), 1)
        self.assertEqual(Run(world, old_save).did("SetVehicle"), [], "the repair runs only once")

    def test_daryl_tomb_entrance_uses_recruited_setzer_not_active_party(self):
        # WC event/daryl_tomb.py gates on Setzer's recruited bit and changes both entrance
        # animations from Setzer to PARTY0.  The vanilla PR map instead hides the trigger behind
        # its Setzer story flag, which WC recruitment does not set.
        st, _ = self.new_game()
        st.party = [3, 4, 12, 5]                 # the user's party: Setzer is recruited, not active
        closed = Run(self.script("sc_map_30650"), st,
                     syscalls={"WC:gate daryl_tomb": gate(False)})
        self.assertIn("ev_e_0001", [t[3][0] for t in closed.did("SetEntities")])
        self.assertNotIn("ev_e_0682", [t[3][0] for t in closed.did("SetEntities")])

        shown = Run(self.script("sc_map_30650"), st,
                    syscalls={"WC:gate daryl_tomb": gate(True)})
        self.assertIn("ev_e_0682", [t[3][0] for t in shown.did("SetEntities")],
                      "recruited Setzer exposes the entrance trigger")
        opened = Run(self.script("sc_e_0682"), st,
                     syscalls={"WC:gate daryl_tomb": gate(True)})
        self.assertEqual(st.get("ScenarioFlag2", 84), 1)
        self.assertIn("ev_e_0682_1", [t[3][0] for t in opened.did("SetEntities")])
        self.assertEqual(opened.did("Msg"), [], "WC removes Daryl's story dialogue")
        puppets_used = [t[2][1] for t in opened.did("SetPuppet")]
        self.assertIn(-1001, puppets_used, "the active party leader opens the tomb")
        self.assertNotIn(-10, puppets_used, "Setzer need not be in the active party")

        # A completed check repairs the open entrance on old/progressed saves.
        repaired, _ = self.new_game()
        repaired.set(E.WC_FLAGS, E.CHECK_FLAGS["daryl_tomb"], 1)
        r = Run(self.script("sc_map_30650"), repaired,
                syscalls={"WC:gate daryl_tomb": gate(False)})
        self.assertEqual(repaired.get("ScenarioFlag2", 84), 1)
        self.assertIn("ev_e_0682_1", [t[3][0] for t in r.did("SetEntities")])

    def test_moogle_defense_is_offered_by_arvis_and_keeps_the_party(self):
        # SNES WC puts Arvis in his house and starts the check only when he is spoken to. Entering
        # the mines from the south or north must not ask. Accepting moves to the mines on black,
        # then the opening runs; party 1 is composed from the entire saved party, not just its lead.
        st, _ = self.new_game()
        mines = Run(self.script("sc_map_30011_2"), st, syscalls={"WC:gate narshe_moogle_defense": gate(True)})
        self.assertEqual((mines.did("SetTimer"), mines.did("ChangeMap")), ([], []), "the mines never offer it")

        house = Run(self.script("sc_map_20011_14"), st,
                    syscalls={"WC:gate narshe_moogle_defense": gate(True)})
        self.assertIn("ev_e_0017", [t[3][0] for t in house.did("SetEntities")], "Arvis is in his house")
        arvis = load(self.assets["ev_e_0017"])
        entity = next(o for layer in arvis["layers"] for o in layer.get("objects", [])
                      if any(p.get("name") == "entity_id" and p.get("value") == 12 for p in o.get("properties", [])))
        props = {p["name"]: p["value"] for p in entity["properties"]}
        self.assertEqual((props["action_id"], props["script_id"]), (2, 39), "Arvis talks through the offer")

        no = Run(self.script("sc_e_0017_5"), st, answers=[1])
        self.assertEqual((no.end, no.did("FadeOut"), no.did("ChangeMap")), (("Exit",), [], []))
        self.assertEqual(st.get(E.WC_FLAGS, E.MOOGLE_OFFERED), 0)
        yes = Run(self.script("sc_e_0017_5"), st, answers=[0])
        self.assertEqual(yes.end, ("ChangeMap", 318, 1, "sc_e_0017_6"))
        self.assertEqual(st.get(E.WC_FLAGS, E.MOOGLE_OFFERED), 1)
        opening = Run(self.script("sc_e_0017_6"), st)
        self.assertEqual(opening.end, ("ChangeMap", 584, 1, "sc_e_0017_7"))
        self.assertEqual(st.get(E.WC_FLAGS, E.MOOGLE_OFFERED), 0)
        self.assertEqual([t[1] for t in opening.trace if t[1] in ("SetEntities", "FadeIn")][:2],
                         ["SetEntities", "FadeIn"])
        opening_script = self.script("sc_e_0017_6")
        reset_at = next(i for i, m in enumerate(opening_script["Mnemonics"])
                        if m.get("label") == "wc_terra_default")
        reset = opening_script["Mnemonics"][reset_at:reset_at + 7]
        terra = next(m for m in reset if m["mnemonic"] == "SetPuppet")
        pose = opening_script["Mnemonics"][terra["operands"]["iValues"][2] + 1]
        self.assertEqual(terra["operands"]["iValues"][1], -1, "the collapsed Terra entity is released")
        self.assertEqual((pose["mnemonic"], pose["operands"]["sValues"][0]), ("CueAnim", "Default"),
                         "a recruited Terra does not keep the horizontal Fall pose")

        party = self.script("sc_e_0017_7")
        calls = [m["operands"]["sValues"][0] for m in party["Mnemonics"] if m["mnemonic"] == "SysCall"]
        self.assertIn("WC:moogle_party_save", calls)
        self.assertIn("WC:moogle_party", calls)
        self.assertNotIn([1, -2, 19, 20, 21],
                         [m["operands"]["iValues"][:5] for m in party["Mnemonics"] if m["mnemonic"] == "OrganizePartySlot"])

        # The user's Deck, 2026-11-19: Mog in the active party was put in party 2 as well. With
        # WC:moogle_party's answer (SCRATCH 1: Mog is in party 1) guest 21 takes his place there.
        def parties(mog_in_party_1):
            st2, _ = self.new_game()
            run = Run(party, st2, syscalls={
                "WC:moogle_party": lambda s: s.set(E.WC_FLAGS, E.SCRATCH, 1 if mog_in_party_1 else 0)})
            return [tuple(t[2][:5]) for t in run.trace if t[1] == "OrganizePartySlot"]
        self.assertEqual(parties(False), [(2, 11, 22, 23, 24), (3, 25, 26, 27, 28)], "Mog's own party otherwise")
        self.assertEqual(parties(True), [(2, 21, 22, 23, 24), (3, 25, 26, 27, 28)], "Mog in one party only")

        # The user's Deck, 2026-11-19 ("i lost mog"): vanilla's moogles-leave call kept only party
        # 1's leader and made Mog unselectable, and the one-party picker opened without him. Party
        # 1 is noted before that call and put back after it, before the reward and the picker.
        ending = self.script("sc_e_0018_2")
        st3, _ = self.new_game()
        end = Run(ending, st3)
        order = [t[3][0] if t[1] == "SysCall" else t[1] for t in end.trace if t[1] in ("SysCall", "Msg")]
        noted, dismissed = order.index("WC:moogle_party_end"), next(i for i, c in enumerate(order) if str(c).startswith("メンバー離脱"))
        restored, picker = order.index("WC:moogle_party_restore"), order.index(E.PARTY_MENU)
        self.assertLess(noted, dismissed, "party 1 is noted before the moogles leave")
        self.assertGreater(restored, dismissed, "party 1 and Mog are restored after the moogles leave")
        self.assertLess(restored, order.index("Msg"), "restored before the reward's line")
        self.assertLess(restored, picker, "restored before the one-party picker opens")

    def test_tritoch_beaten_leaves_the_lone_wolf_chase_in_the_wob(self):
        # The user's Deck, 2026-11-14: "killing tritech blocks me from doing the moogle lone wolf
        # check". Narshe's cliffs (sc_map_20040) asked "Tritoch done" first, in either world.
        st, _ = self.new_game()
        st.set("ScenarioFlag2", 139, 1)                  # Tritoch beaten (in the WoR)
        st.set("ScenarioFlag1", 212, 1)                  # the chase is on (WC's start sets it)
        layers = lambda r: [t[3][0] for t in r.did("SetEntities")]
        sc = self.script("sc_map_20040")
        self.assertIn("ev_e_0436", layers(Run(sc, st)), "the WoB: the Lone Wolf chase")
        st.set("ScenarioFlag2", 97, 1)
        st.set("ScenarioFlag2", 41, 1)
        self.assertEqual(layers(Run(sc, st)), ["ev_e_0781"], "the WoR: the way down to Umaro's cave")
        vanilla = State()
        vanilla.set("ScenarioFlag2", 139, 1)
        vanilla.set("ScenarioFlag1", 212, 1)
        self.assertEqual(layers(Run(sc, vanilla)), ["ev_e_0609", "ev_e_0781"], "vanilla unchanged")

    def test_narshe_snowfield_has_no_character_lineup(self):
        # The user's Deck, 2026-11-14: "the narshe defense, all the characters are lined up and i
        # can't actually move past them". WC has no lineup: Banon and the save point (ev_e_0250)
        # with the six swap spots and the characters on them hidden, none placed.
        st, _ = self.new_game()
        sc = self.script("sc_map_20030")
        r = Run(sc, st)
        self.assertEqual(r.end, ("Exit",))
        self.assertEqual(len(r.did("SetEntities", "ev_e_0250")), 1, "Banon is there")
        hidden = [t[2][1] for t in r.did("SetPuppet")]
        self.assertEqual(sorted(hidden), sorted(E.NARSHE_LINEUP))
        labels = {m["label"] for m in sc["Mnemonics"] if m["label"]}
        called = {sc["Mnemonics"][t[2][0]]["label"] for t in r.did("Call")}
        self.assertFalse({c for c in called if c.startswith("position_")}, "nobody stood in the row")
        self.assertIn("position_tena1", labels)
        # vanilla, between the scenarios' end and the battle: the lineup stands
        vanilla = State()
        vanilla.set("ScenarioFlag1", 136, 1)
        vanilla.set("MapLocalVariable", 0, 1)          # Terra's slot in the row: first from the left
        rv = Run(sc, vanilla)
        self.assertIn("position_tena1", {sc["Mnemonics"][t[2][0]]["label"] for t in rv.did("Call")},
                      "vanilla places the lineup")
        self.assertFalse([t for t in rv.did("Call") if sc["Mnemonics"][t[2][0]]["label"] == "wc_hide_lineup"])

    def test_gau_father_wor_old_man_without_gau_flicker(self):
        # The WoR house (sc_e_0959, map 314) isn't WC's check (that's the WoB house, map 73). With
        # Gau in the party but not leading, vanilla shows Gau in the leader's place for a line
        # the WC game skips, then the leader again: the user saw Shadow turn into Gau and back
        # (2026-11-14). A WC game: the old man's lines only.
        st, _ = self.new_game()
        st.party = [3, 12]
        r = Run(self.script("sc_e_0959"), st)
        self.assertEqual(r.did("Call"), [], "no Gau shown or hidden")
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["E0959_00_314_a_05", "E0959_00_314_a_04"])
        vanilla = State()
        vanilla.party = [3, 12]
        self.assertEqual(len(Run(self.script("sc_e_0959"), vanilla).did("Call")), 2, "vanilla shows Gau")

    def test_checks_that_stay_in_the_dungeon_keep_the_warp_record(self):
        # Vanilla clears the Warp record (RemoveTelepoCache) before scenes that fly the party
        # away (the Veldt cave's Shadow/Relm scene to Thamasa, Daryl's tomb's Falcon). WC leaves
        # the party in the dungeon after the reward, so a Warp Stone there had nowhere to go: a
        # black screen (the user's game, 2026-11-14, the Veldt cave after the Shadow check).
        for shadow, scene in ((1, "sc_e_0744_1"), (0, "sc_e_0745_1")):
            st, _ = self.new_game()
            st.set("ScenarioFlag2", 42, shadow)
            r = Run(self.script("sc_e_0744"), st, syscalls={"WC:gate veldt_cave_wor": gate(True)})
            self.assertEqual(r.end, ("ChangeMap", 491, 3, scene))
            self.assertEqual(r.did("RemoveTelepoCache"), [], scene)
            vanilla = State()
            vanilla.set("ScenarioFlag2", 42, shadow)
            self.assertEqual(len(Run(self.script("sc_e_0744"), vanilla).did("RemoveTelepoCache")), 1, f"vanilla {scene}")
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0703"), st, syscalls={"WC:gate daryl_tomb": gate(True)})
        self.assertEqual(len(r.did("SysCall", "WC:reward daryl_tomb 0")), 1)
        self.assertEqual(r.did("RemoveTelepoCache"), [])
        self.assertEqual(len(Run(self.script("sc_e_0703"), State()).did("RemoveTelepoCache")), 1, "vanilla Daryl's tomb")

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
        opening = Run(self.script("sc_e_0055"), st, syscalls={"WC:gate mt_kolts": gate(True)})
        self.assertEqual(opening.did("Call"), [], "no vanilla party-puppet scene before Vargas")
        self.assertEqual(opening.did("Msg"), [], "no vanilla Vargas/Sabin scene")
        self.assertEqual([t[2][0] for t in opening.did("EncountBoss")], [436])
        r = Run(self.script("sc_e_0055_1"), st)
        # the restore pairs with the save in the fight's Sabin event, which a WC game no longer
        # plays (boss_battle_events, 2026-11-12): nothing to restore
        self.assertFalse(r.did("SysCall", "バトルデータ復元：sc_b_0055"), "no battle data restored")
        self.assertEqual(len(r.did("SysCall", "WC:reward mt_kolts 0")), 1)
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["WC_REWARD"], "no Sabin reunion dialogue")
        self.assertEqual(r.did("Call"), [], "no Sabin reunion animation")
        self.assertEqual(st.get("ScenarioFlag1", 41), 1, "Vargas defeated")
        self.assertEqual(st.get(E.WC_FLAGS, E.MT_KOLTS_REPAIRED), 1)
        self.assertEqual(r.end, ("ChangeMap", 343, 2, ""), "reload the map without scene puppets")
        self.assertEqual(r.black_lines, [], "the reward is visible before the reload")

    def test_mt_kolts_repairs_old_puppet_save_once(self):
        st, _ = self.new_game()
        st.set("ScenarioFlag1", 41, 1)
        first = Run(self.script("sc_map_30360"), st)
        self.assertEqual(first.end, ("ChangeMap", 343, 2, ""))
        self.assertEqual(st.get(E.WC_FLAGS, E.MT_KOLTS_REPAIRED), 1)
        second = Run(self.script("sc_map_30360"), st)
        self.assertEqual(second.end, ("Exit",), "the one-time repair cannot reload recursively")
        self.assertEqual(second.did("ChangeMap"), [])

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
        scene = self.script("sc_e_0639")
        r = Run(scene, st)
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["WC_REWARD"],
                         "WC removes every post-escape story line")
        self.assertEqual(len(r.did("SysCall", "WC:reward collapsing_house 0")), 1)
        self.assertEqual(st.get("ScenarioFlag2", 56), 1)
        self.assertEqual(st.get(E.WC_FLAGS, E.COLLAPSING_HOUSE_REPAIRED), 1)
        self.assertEqual(r.end, ("ChangeMap", 201, 1, ""), "reload Tzen to restore field movement")
        self.assertEqual(r.did("SetPuppet"), [], "the shortened ending never puppets PARTY0")
        self.assertEqual(r.did("AssignMoveRoute"), [], "no vanilla town cutscene movement remains")
        self.assertIn("ev_e_0639", [t[3][0] for t in r.did("SetEntities")], "the completed town layer")
        # the house's quake (sc_e_0634: Shake iValues[3], re-run on every map) stops as vanilla's
        # skipped scene stops it (sc_e_0639: 19), or the whole game shakes after the check
        self.assertEqual([t[2][0] for t in r.did("Shake")], [0], "the quake stops")

    def test_collapsing_house_repairs_old_stuck_save_once(self):
        st, _ = self.new_game()
        st.set("ScenarioFlag2", 56, 1)  # completed by either of the old shortened endings
        first = Run(self.script("sc_map_20330"), st)
        self.assertEqual(first.end, ("ChangeMap", 201, 1, ""), "one town reload releases field input")
        self.assertEqual(st.get(E.WC_FLAGS, E.COLLAPSING_HOUSE_REPAIRED), 1)
        second = Run(self.script("sc_map_20330"), st)
        self.assertEqual(second.end, ("Exit",), "the repair cannot loop on the reloaded map")
        self.assertEqual(second.did("ChangeMap"), [])

    def test_floating_continent_escape_stops_the_quake(self):
        """The user's Deck (2026-11-12): "after i completed the floating continent the whole game
        rumbles now". The escape's maps start a quake that re-runs on every map (sc_e_0603_1-0606_1,
        Shake iValues[3]); vanilla stops it on the world map after the collapse (sc_e_0607_7),
        which WC drops: its end back on the Blackjack stops it."""
        st, _ = self.new_game()
        r = Run(self.script("sc_e_0607_1"), st)
        self.assertEqual(len(r.did("SysCall", "WC:reward floating_continent 2")), 1)
        stops = [t for t in r.did("Shake") if t[2][0] == 0]
        self.assertEqual(len(stops), 1, "the quake stops")
        self.assertEqual(r.end[:2], ("ChangeMap", E.DECK_MAP), r.end)
        self.assertEqual(r.trace[-1][1], "ChangeMap")
        self.assertGreater(stops[0], r.did("SysCall", "WC:reward floating_continent 2")[0], "after the reward")

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
        # Lone Wolf's cliff is a choice; the unchosen reward stays for the WoR Moogle Cave
        st, _ = self.new_game()
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["magitek_factory"], 1)
        r = Run(self.script("sc_e_0401"), st, syscalls={"WC:gate magitek_factory": gate(True)})
        self.assertTrue(r.did("EncountBoss") or r.did("Encount"), "Number 024 fights after the first reward")
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["magitek_factory_2"], 1)
        r = Run(self.script("sc_e_0401"), st, syscalls={"WC:gate magitek_factory": gate(True)})
        self.assertEqual((r.did("EncountBoss"), r.did("Encount"), r.end), ([], [], ("Exit",)), "not twice")
        fresh, _ = self.new_game()
        wolf = Run(self.script("sc_e_0439"), fresh, syscalls={"WC:gate lone_wolf": gate(True)})
        self.assertEqual(len(wolf.did("SysCall", "WC:lone_wolf_taunt E0439_00_020_a_01")), 1,
                         "Lone Wolf names the randomized second reward")
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["lone_wolf"], 1)
        st.set("ScenarioFlag1", 215, 1)
        r = Run(self.script("sc_e_0439"), st, syscalls={"WC:gate lone_wolf": gate(True)})
        self.assertEqual(r.did("SysCall", "WC:reward lone_wolf 1"), [], "the other cliff reward is gone")
        cave = Run(self.script("sc_e_0778"), st)
        self.assertEqual(len(cave.did("SysCall", "WC:reward lone_wolf 1")), 1,
                         "the unchosen item moves to the WoR Moogle Cave")

        st2, _ = self.new_game()
        st2.set(E.WC_FLAGS, E.CHECK_FLAGS["lone_wolf_2"], 1)
        st2.set("ScenarioFlag1", 216, 1)
        moogle = Run(self.script("sc_e_0438"), st2, syscalls={"WC:gate lone_wolf": gate(True)})
        self.assertEqual(moogle.did("SysCall", "WC:reward lone_wolf 0"), [], "the other cliff reward is gone")
        cave = Run(self.script("sc_e_0778"), st2)
        self.assertEqual(len(cave.did("SysCall", "WC:reward lone_wolf 0")), 1,
                         "the unchosen first reward moves to the WoR Moogle Cave")

    def test_ifrit_shiva_battle_gives_reward_and_clears_both(self):
        """WC rewards this check after the battle, before PR's talk-to-both/magicite state."""
        st, _ = self.new_game()
        start = Run(self.script("sc_e_0394"), st,
                    syscalls={"WC:gate magitek_factory": gate(True)})
        self.assertEqual([t[2][0] for t in start.did("EncountBoss")], [440])

        won = Run(self.script("sc_e_0394_1"), st,
                  syscalls={"WC:reward magitek_factory 0": lambda state: None})
        self.assertEqual(len(won.did("SysCall", "WC:reward magitek_factory 0")), 1)
        self.assertEqual(st.get(E.WC_FLAGS, E.CHECK_FLAGS["magitek_factory"]), 1)
        self.assertEqual([st.get("ScenarioFlag1", f) for f in (184, 188, 189, 190)], [1, 1, 1, 1])
        self.assertEqual([t[2][0] for t in won.did("Call")],
                         [next(i for i, m in enumerate(self.script("sc_e_0394_1")["Mnemonics"])
                               if m.get("label") == "wc_hide_espers")])

        room = Run(self.script("sc_map_30521_3"), st)
        shown = [t[3][0] for t in room.did("SetEntities")]
        self.assertFalse(set(shown) & {"ev_e_0394", "ev_e_0396", "ev_e_0399", "ev_e_0400"}, shown)

        # Repair a save made after the old battle callback: talking to either remaining esper.
        old, _ = self.new_game()
        old.set("ScenarioFlag1", 184, 1)
        repaired = Run(self.script("sc_e_0395"), old,
                       syscalls={"WC:reward magitek_factory 0": lambda state: None})
        self.assertEqual(len(repaired.did("SysCall", "WC:reward magitek_factory 0")), 1)
        self.assertEqual(old.get(E.WC_FLAGS, E.CHECK_FLAGS["magitek_factory"]), 1)
        self.assertEqual(repaired.did("Msg")[0][3][0], "WC_REWARD")

    def test_magitek_factory_end_is_wc_s(self):
        # WC event/magitek_factory.py: the tube switch gives no reward. It skips the tube/Celes,
        # Cid and elevator scenes into the mine cart; reward 3 is at the former Setzer meetup,
        # followed by the Cranes and the airship outside Vector.
        st, _ = self.new_game()
        st.party = [7, 1]                                   # Celes in the party
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["magitek_factory"], 1)
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["magitek_factory_2"], 1)
        r = Run(self.script("sc_e_0402"), st, syscalls={"WC:gate magitek_factory": gate(True)})
        self.assertEqual(r.did("SysCall", "WC:reward magitek_factory 2"), [], "no reward at the tubes")
        self.assertEqual(r.did("SysCall", "メンバー離脱：セリス"), [], "Celes doesn't leave")
        self.assertEqual(r.did("Msg"), [], "no tube story")
        self.assertEqual(r.did("GetItem"), [], "no tube espers")
        self.assertEqual([st.get("ScenarioFlag1", f) for f in (192, 193, 177)], [1, 1, 1])
        self.assertEqual(st.get(E.WC_FLAGS, E.CHECK_FLAGS["magitek_factory_3"]), 0)
        self.assertEqual(r.end, ("ChangeMap", 578, 1, "sc_e_0404_1"), "straight into the mine cart")

        # The user's old port save already received reward 3 in the tube room; it still escapes
        # without receiving it twice.
        repaired = copy.deepcopy(st)
        repaired.set(E.WC_FLAGS, E.CHECK_FLAGS["magitek_factory_3"], 1)
        old = Run(self.script("sc_e_0402"), repaired,
                  syscalls={"WC:gate magitek_factory": gate(True)})
        self.assertEqual(old.did("SysCall", "WC:reward magitek_factory 2"), [])
        self.assertEqual(old.end, ("ChangeMap", 578, 1, "sc_e_0404_1"))

        r = Run(self.script("sc_e_0403"), st)
        self.assertEqual((r.end, st.get("ScenarioFlag1", 193)), (("ChangeMap", 404, 1, ""), 1), "straight down to Cid")
        r = Run(self.script("sc_e_0404"), st)
        self.assertEqual((r.did("Msg"), r.end), ([], ("ChangeMap", 578, 1, "sc_e_0404_1")), "talking to Cid: the cart")
        self.assertEqual(st.get("ScenarioFlag1", 194), 0, "no escape state (it brings Setzer's scene back to Vector)")

        r = Run(self.script("sc_e_0404_8"), st,
                syscalls={"WC:reward magitek_factory 2": lambda state: None})
        self.assertEqual(len(r.did("SysCall", "WC:reward magitek_factory 2")), 1,
                         "reward 3 is after the mine cart")
        self.assertEqual([t[3][0] for t in r.did("Msg")], ["WC_REWARD"])
        self.assertEqual(st.get(E.WC_FLAGS, E.CHECK_FLAGS["magitek_factory_3"]), 1)
        self.assertEqual(r.did("SetEntities", "ev_e_0405"), [], "no Setzer in Vector")
        boss = r.did("EncountBoss")
        self.assertEqual([(t[2][0], t[3][0]) for t in boss], [(E.CRANES, "sc_e_0405")], "the Cranes, then out")
        self.assertFalse(r.black, "the fight starts from the field")

        already = Run(self.script("sc_e_0404_8"), st)
        self.assertEqual(already.did("SysCall", "WC:reward magitek_factory 2"), [], "no duplicate reward")
        self.assertEqual([(t[2][0], t[3][0]) for t in already.did("EncountBoss")],
                         [(E.CRANES, "sc_e_0405")])
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

    def test_imperial_observation_post_matches_wc(self):
        sc = self.script("sc_map_20240")
        for opened in (False, True):
            with self.subTest(terra_recruited=opened):
                st, _ = self.new_game()
                r = Run(sc, st, syscalls={"WC:gate sealed_gate": gate(opened)})
                groups = [t[3][0] for t in r.did("SetEntities")]
                self.assertNotIn("ev_e_0350", groups, "WC never loads the vanilla soldier encounters")
                self.assertNotIn("ev_e_0441", groups, "WC does not require Terra in the active party")
                self.assertNotIn("ev_e_0442", groups, "WC skips Terra's vanilla post scene")
                self.assertEqual(groups, [] if opened else ["ev_e_0440"],
                                 "open passage, or only the Terra-not-recruited boundary")
                self.assertEqual(st.get("ScenarioFlag2", 4), int(opened),
                                 "the armory is locked without Terra and open once recruited")
        vanilla = State()
        self.assertIn("ev_e_0350", [t[3][0] for t in Run(sc, vanilla).did("SetEntities")],
                      "a non-WC game keeps the soldiers")

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
        self.assertEqual(([t[3][0] for t in r.did("Msg")], r.end),
                         (["WC_REWARD"], ("ChangeMap", E.WOB_MAP, 1, "sc_e_BJoutboard_1")),
                         "opera: reward, then native boarding into the flying Blackjack")
        st, r = run("sc_e_0639", black=True)
        self.assertEqual(([t[3][0] for t in r.did("Msg")], len(r.did("FinishTimer")), r.black, r.end),
                         (["WC_REWARD"], 1, True, ("ChangeMap", 201, 1, "")))
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

    def test_owzer_mansion_switch_exists_in_wor_and_is_gated_by_relm(self):
        """WC never runs vanilla's captured-Relm/diary story, so its flags cannot be required
        to create Owzer's light switch. The switch belongs to the WoR and Relm gates it."""
        room = self.script("sc_map_30631_1")
        st, _ = self.new_game()
        self.assertEqual(Run(room, st).did("SetEntities", "ev_e_0758"), [], "not in the WoB")
        st.set("ScenarioFlag2", 41, 1)
        entered = Run(room, st)
        self.assertEqual(len(entered.did("SetEntities", "ev_e_0758")), 1, "switch in the WoR")
        self.assertEqual(len(entered.did("SetEntities", "ev_e_0762")), 1, "woman portrait in the WoR")

        light = self.script("sc_e_0758")
        shut = Run(light, st, syscalls={"WC:gate owzer_mansion": gate(False)})
        self.assertEqual(([t[3][0] for t in shut.did("Msg")], st.get("MapLocalVariable", 2)),
                         (["E0756_00_442_a_01"], 0), "without Relm: stay away and leave lights on")
        opened = Run(light, st, syscalls={"WC:gate owzer_mansion": gate(True)})
        self.assertEqual(st.get("MapLocalVariable", 2), 1, "Relm recruited: switch turns the lights off")
        self.assertTrue(opened.did("ColorFade"))

        portrait = self.script("sc_e_0762")
        shut = Run(portrait, st, syscalls={"WC:gate owzer_mansion": gate(False)})
        self.assertEqual(([t[3][0] for t in shut.did("Msg")], shut.did("Encount")),
                         (["E0756_00_442_a_01"], []),
                         "without Relm: portrait stays closed")
        opened = Run(portrait, st, syscalls={"WC:gate owzer_mansion": gate(True)})
        self.assertEqual([t[2][0] for t in opened.did("Encount")], [289],
                         "Relm recruited: woman portrait starts the hidden-door battle")

    def test_owzer_reward_follows_chadarnook_and_repairs_old_save(self):
        """WC removes the bookshelf stop: the post-battle callback gives the reward. An old
        save with Chadarnook defeated but the WC flag unset gets it by talking to Owzer."""
        st, _ = self.new_game()
        sys_ = {"WC:reward owzer_mansion 0": lambda state: None}
        won = Run(self.script("sc_e_0773_2"), st, syscalls=sys_)
        self.assertEqual((len(won.did("SysCall", "WC:reward owzer_mansion 0")),
                          [t[3][0] for t in won.did("Msg")],
                          st.get(E.WC_FLAGS, E.CHECK_FLAGS["owzer_mansion"]), st.get("ScenarioFlag2", 135), won.end),
                         (1, ["WC_REWARD"], 1, 1, ("Exit",)), "fresh victory rewards immediately")

        old, _ = self.new_game()
        old.set("ScenarioFlag2", 134, 1)          # Chadarnook already defeated by the old port
        repaired = Run(self.script("sc_e_0773"), old, syscalls=sys_)
        self.assertEqual((len(repaired.did("SysCall", "WC:reward owzer_mansion 0")),
                          [t[3][0] for t in repaired.did("Msg")],
                          old.get(E.WC_FLAGS, E.CHECK_FLAGS["owzer_mansion"]), old.get("ScenarioFlag2", 135)),
                         (1, ["WC_REWARD"], 1, 1), "Owzer repairs the already-defeated state")
        self.assertNotIn("E0773_02_446_a_06", [t[3][0] for t in repaired.did("Msg")])

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
                         (["E0781_00_020_a_01"], ("ChangeMap", 493, 1, "sc_e_0781_1")),
                         "Umaro joined: one prompt, then the vanilla jump")
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
                st.set("ScenarioFlag1", 13, 1)  # a progressed save / after the moogle defense
                r = Run(sc, st)
                groups = [t[3][0] for t in r.did("SetEntities")]
                self.assertNotIn("ev_e_0001", groups, "no Magitek walls in a WC game")
                self.assertIn("ev_e_0609_1" if world else "ev_e_0609", groups, "the floor's exits")
                if world:
                    self.assertNotIn("ev_e_0011", groups, "Whelk is WoB-only")
                else:
                    self.assertIn("ev_e_0011", groups, "Whelk is at PR's ordinary WoB gate")
        vanilla = State()
        self.assertIn("ev_e_0001", [t[3][0] for t in Run(sc, vanilla).did("SetEntities")], "the opening: untouched")

    def test_whelk_is_at_the_pr_wc_mine_gate(self):
        st, _ = self.new_game()
        mine = self.script("sc_map_30021_1")
        groups = lambda state: [t[3][0] for t in Run(mine, state).did("SetEntities")]
        self.assertIn("ev_e_0011", groups(st), "the guard appears at the gate in the screenshots")
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["whelk"], 1)
        self.assertIn("ev_e_0011", groups(st), "a stale WC bit does not hide Whelk")
        self.assertEqual(st.get(E.WC_FLAGS, E.CHECK_FLAGS["whelk"]), 0, "the stale WC bit is repaired")
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["whelk"], 1)
        st.set("ScenarioFlag1", 9, 1)
        self.assertNotIn("ev_e_0011", groups(st), "a genuinely finished Whelk guard disappears")
        st.set(E.WC_FLAGS, E.CHECK_FLAGS["whelk"], 0)
        st.set("ScenarioFlag1", 9, 0)
        st.set(*COLLAPSED, 1)
        self.assertNotIn("ev_e_0011", groups(st), "the WoR mine contains no Whelk guard")

        st, _ = self.new_game()
        fight = Run(self.script("sc_e_0011"), st, syscalls={"WC:gate whelk": gate(True)})
        self.assertEqual([t[2][0] for t in fight.did("EncountBoss")], [433])
        self.assertEqual((fight.did("Msg"), fight.did("Call")), ([], []),
                         "no opening-map Biggs/Wedge or fixed-position puppet scene")
        reward = Run(self.script("sc_e_0011_1"), st,
                     syscalls={"WC:reward whelk 0": lambda state: None})
        self.assertEqual((len(reward.did("SysCall", "WC:reward whelk 0")),
                          st.get(E.WC_FLAGS, E.CHECK_FLAGS["whelk"]), reward.did("Call")),
                         (1, 1, []), "reward without the opening map's party teleport")

    def test_phoenix_cave_has_no_reunion(self):
        st, _ = self.new_game()
        landing = Run(self.script("sc_e_0792_1"), st)
        self.assertEqual(landing.end, ("ChangeMap", 497, 1, "sc_e_0792_2"))
        self.assertEqual(len(landing.did("SysCall", "パーティ編成：２パーティ")), 1)
        self.assertEqual(landing.did("OrganizePartySlot"), [],
                         "WC must not force vanilla Celes into a seed that has not recruited her")
        self.assertEqual(landing.did("Call"), [], "no Falcon puppet/drop cinematic")
        self.assertEqual(landing.did("ChangePartySlot"), [], "the cinematic cannot select an empty second slot")
        self.assertEqual(landing.black_lines, [], "the party menu and transition are visible")

        # ChangeScript at arrival instruction 1 initializes the crane map in the real engine;
        # model its return by starting at the following WC block while the transition is black.
        arrival = self.script("sc_e_0792_2")
        at = next(i for i, m in enumerate(arrival["Mnemonics"]) if m.get("label") == "wc_phoenix_arrive")
        arrived = Run(arrival, st, start=at, black=True)
        self.assertEqual(arrived.end, ("Exit",))
        self.assertFalse(arrived.black, "Phoenix Cave explicitly fades in before field control")
        self.assertEqual([t[2][0] for t in arrived.did("Call")], [20, 27, 37, 48],
                         "both parties run their vanilla position/drop calls before field control")
        self.assertEqual(arrived.did("Msg"), [], "the drop keeps no vanilla story line")
        self.assertEqual(len(arrived.did("SysCall", "パーティ配置：フェニックスの洞窟")), 1)
        self.assertEqual((st.get("ScenarioFlag4", 197), st.get("ScenarioFlag3", 45)), (1, 1))

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

    def test_blackjack_unequip_npc_also_changes_party(self):
        """WC adds party organization to the Blackjack NPC without changing vanilla saves or
        removing either equipment-removal service."""
        sc = self.script("sc_e_0345")

        st, _ = self.new_game()
        changed = Run(sc, st, answers=[E.AIRSHIP_PARTY_CHOICE])
        self.assertEqual(changed.offered[0], [E.AIRSHIP_PARTY_CHOICE,
                         "E0345_00_120_c_01", "E0345_00_120_c_02", "E0345_00_120_c_03"])
        self.assertEqual(len(changed.did("SysCall", E.PARTY_MENU)), 1)
        self.assertEqual(len(changed.did("SysCall", "WC:party_refresh")), 1)
        self.assertEqual(changed.end, ("Exit",))

        reserve = Run(sc, st, answers=["E0345_00_120_c_01"])
        everyone = Run(sc, st, answers=["E0345_00_120_c_02"])
        self.assertEqual(len(reserve.did("SysCall", "\u63a7\u3048\u30e1\u30f3\u30d0\u30fc\u306e\u88c5\u5099\u3092\u5916\u3059")), 1)
        self.assertEqual(len(everyone.did("SysCall", "\u5168\u54e1\u306e\u88c5\u5099\u3092\u5916\u3059")), 1)

        vanilla = Run(sc, State())
        self.assertEqual(vanilla.offered[0], ["E0345_00_120_c_01",
                         "E0345_00_120_c_02", "E0345_00_120_c_03"])
        self.assertEqual(vanilla.did("SysCall", E.PARTY_MENU), [])

    def test_falcon_unequip_npc_also_changes_party(self):
        """The Falcon's copies of the same NPC provide the same WC party service."""
        for name in ("sc_e_0678", "sc_e_0704"):
            sc = self.script(name)
            st, _ = self.new_game()
            changed = Run(sc, st, answers=[E.AIRSHIP_PARTY_CHOICE])
            self.assertEqual(changed.offered[0], [E.AIRSHIP_PARTY_CHOICE,
                             "E0345_00_120_c_01", "E0345_00_120_c_02", "E0345_00_120_c_03"], name)
            self.assertEqual(len(changed.did("SysCall", E.PARTY_MENU)), 1, name)
            self.assertEqual(len(changed.did("SysCall", "WC:party_refresh")), 1, name)

            vanilla = Run(sc, State())
            self.assertEqual(vanilla.offered[0], ["E0345_00_120_c_01",
                             "E0345_00_120_c_02", "E0345_00_120_c_03"], name)
            self.assertEqual(vanilla.did("SysCall", E.PARTY_MENU), [], name)

    def test_unequip_npcs_take_umaro_with_eu(self):
        """-eu (WC event/airship.py equipable_umaro): each airship NPC's two unequip services run
        the PR's own call (which skips only Umaro) and then WC:eu_unequip, where the plugin takes
        Umaro's equipment when the seed has -eu; outside a WC game, the PR's call alone."""
        for name in ("sc_e_0345", "sc_e_0678", "sc_e_0704"):
            sc = self.script(name)
            for answer, call, which in (("E0345_00_120_c_01", E.UNEQUIP_RESERVE, "reserve"),
                                        ("E0345_00_120_c_02", E.UNEQUIP_ALL, "all")):
                st, _ = self.new_game()
                r = Run(sc, st, answers=[answer])
                calls = [t[3][0] for t in r.did("SysCall")]
                self.assertEqual(calls, [call, f"WC:eu_unequip {which}"], f"{name} {which}")
                self.assertEqual(r.end, ("Exit",), f"{name} {which}")
                vanilla = Run(sc, State(), answers=[answer])
                self.assertEqual([t[3][0] for t in vanilla.did("SysCall")], [call], f"{name} {which}, not a WC game")
            st, _ = self.new_game()
            nothing = Run(sc, st, answers=["E0345_00_120_c_03"])
            self.assertEqual(nothing.did("SysCall"), [], f"{name}: don't do a thing")

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
        import prdata as P
        # the porter's tables, as port.py passes them (kit 22664ae: a scripted gil gift needs SetGilTag's price group)
        PC.port(self.van, self.wc, out, lambda *a: None, {})

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

    def test_chadarnook_wc_ai_signals_the_pr_form_switch(self):
        """The PR's sc_b_2016 owns Chadarnook's NoSelectChad form animation, so the WC AI's
        F5 painting/demon swaps are removed.  Its replacement must set AI variable 9, the
        same handshake used by both original PR AIs; without it the painting stays active
        forever and the fight cannot be won."""
        import ai_compile as AC
        import snes_ai as A

        for enemy in (286, 328):
            main, counter = A.script(self.wc, enemy)
            ctx = AC.Context(enemy, 1, 1, pr_entrances=True)
            ms = AC.compile_script(ctx, main, counter)
            signals = [m for m in ms if m["mnemonic"] == "VariableAssign"
                       and m["operands"]["iValues"][:2] == [9, 1]]
            self.assertTrue(signals, f"Chadarnook form {enemy} never signals sc_b_2016")


class EventLooks(unittest.TestCase):
    def test_nelapa_pr_opening_is_skipped_in_wc(self):
        # A shuffled Nelapa copy uses sc_b_2019 outside party 452.  The PR-only
        # Doom/laugh syscall then repeats forever; WC's SNES AI supplies the
        # opening once instead.
        import wc_events
        made = wc_events.nelapa_battle()
        self.assertEqual(len(made), 1)
        ms = made[0][0]["Mnemonics"]
        hook = next(i for i, m in enumerate(ms) if m.get("label") == "wc_snes_doom_once")
        self.assertEqual(ms[hook + 1]["mnemonic"], "Branch")
        self.assertEqual(ms[hook + 2]["mnemonic"], "Jump")
        self.assertEqual(ms[hook + 3]["label"], "vanilla")
        self.assertEqual(ms[hook + 4]["mnemonic"], "SysCall", "vanilla syscall remains behind the WC gate")
        self.assertEqual(ms[hook + 4]["operands"]["sValues"][0], "ネラパ死の宣告")

    def test_ifrit_shiva_is_the_real_fight(self):
        # The user's Deck, seed gaadl68ix7j4, 2026-11-14: Kefka at Narshe swapped for Ifrit/Shiva
        # "fought ifrit ... and the fight immediately ended". SNES formation 439 starts with Ifrit
        # alone (Shiva a hidden slot); matched by name it went to party 392, the Magitek Factory's
        # esper scene (Ifrit shown, a forced exit). The PR's fight is 440 (Ifrit and Shiva).
        import idmap
        self.assertEqual(idmap.party(439), 440)

    def test_seed_upgrade_moves_stale_ifrit_parties(self):
        # wc_seed_upgrade.party_fixes: a seed made with 439 -> 392 gets the factory event keyed as
        # 440, its copy on 440's background, Narshe's copy rebuilt from 440, the boss list fixed
        import wc_seed_upgrade as U
        head = ["id", "battle_background_asset_id", "battle_bgm_asset_id", "script_name", "monster1"]
        rows = [["392", "31", "0", "8017", "552"], ["440", "30", "25", "9009", "266"], ["485", "12", "25", "9021", "323"],
                ["506", "19", "25", "0", "331"], ["20033", "31", "25", "9021", "323"], ["20076", "19", "0", "8017", "552"]]
        tmp = tempfile.mkdtemp(prefix="wc_upgrade_test_")
        try:
            os.makedirs(os.path.join(tmp, os.path.dirname(U.MONSTER_PARTY)))
            with open(os.path.join(tmp, U.MONSTER_PARTY), "w", encoding="utf-8", newline="") as f:
                f.write("\r\n".join(",".join(r) for r in [head] + rows) + "\r\n")
            with open(os.path.join(tmp, "wc_seed.json"), "w", encoding="utf-8") as f:
                json.dump({"battles": {"392": [20033], "506": [20076]}, "look_sources": {"20033": 485, "20076": 392},
                           "bosses": [{"name": "Ifrit/Shiva", "party": 392}]}, f)
            battles, sources, bosses, copies = U.party_fixes(tmp)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        self.assertEqual(battles, {"440": [20033], "506": [20076]})
        self.assertEqual(sources, {"20033": 485, "20076": 440})
        self.assertEqual(bosses, [{"name": "Ifrit/Shiva", "party": 440}])
        self.assertEqual(copies["20076"], {"id": "20076", "battle_background_asset_id": "19", "battle_bgm_asset_id": "25",
                                           "script_name": "9009", "monster1": "266"})
        self.assertEqual(copies["20033"]["battle_background_asset_id"], "30")

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
        src = wc_test_source()
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
        src = wc_test_source()
        speeds = (1, 10, 30, 50, 100, 128)
        rows = {"party": [self.rate(x) for x in speeds]}
        for pr_speed, bs in enumerate((5, 4, 2, 1, 0)):
            rows[f"[{pr_speed}]"] = [self.rate(x, True, bs) for x in speeds]
        for key, vals in rows.items():
            text = ", ".join(f"{round(v, 4):g}" for v in vals)
            self.assertIn(text, src, f"{key}: {text}")
        self.assertIn(f"{round(self.rate(50, k=126), 4):g}", src)
        self.assertIn(f"{round(self.rate(50, k=48), 4):g}", src)
        plugin = (open(os.path.join(PR, "plugin", "PrKit", "Ff6", "SnesAtb.cs"), encoding="utf-8").read() + open(os.path.join(PR, "plugin", "PrKit", "Ff6", "SnesAtbRules.cs"), encoding="utf-8").read())
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
        cls.src = open(os.path.join(PR, "plugin", "PrKit", "Ff6", "SnesStatusClock.cs"), encoding="utf-8").read()
        cls.hooks = open(os.path.join(PR, "plugin", "PrKit", "Ff6", "SnesStatusTicks.cs"), encoding="utf-8").read()

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
        atb = (open(os.path.join(PR, "plugin", "PrKit", "Ff6", "SnesAtb.cs"), encoding="utf-8").read() + open(os.path.join(PR, "plugin", "PrKit", "Ff6", "SnesAtbRules.cs"), encoding="utf-8").read())
        self.assertIn("JumpWait = 224 * 256", atb)
        self.assertIn("int perTick = Math.Max(1, AtbAdd(speed, monster, battleSpeed, haste, slow) >> 1);", atb)  # kit SnesAtbRules since 11-20
        self.assertIn("int moves = (wait * 256 + perTick - 1) / perTick;", atb)
        # the PR's landing: ability 22 (type 16, "ContinuousJumpEnd", ability_wait 30) after Jump (21, type 15):
        # its chant time (BattleProgressATBFF6.GetChantingTime) is what the plugin sets to the SNES's air time
        import csv
        rows = {r["id"]: r for r in csv.DictReader(open(os.path.join(PR, "vanilla", "master", "Assets", "GameAssets", "Serial",
                                                                      "Data", "Master", "ability.csv"), encoding="utf-8"))}
        self.assertEqual((rows["21"]["type_id"], rows["21"]["ability_wait"]), ("15", "1"))
        self.assertEqual((rows["22"]["type_id"], rows["22"]["ability_wait"]), ("16", "30"))
        self.assertIn('"GetChantingTime"', atb)
        self.assertIn("chant == SnesAtbRules.Chant.Landing", atb)  # the landing (type 16) via SnesAtbRules.ChantOf since 11-20
        # Speed 33: (33 + 20) x 96 / 16 = 318 a move, 159 to the wait timer: 361 moves, 722 frames (1.75 gauges)
        per = (33 + 20) * 96 >> 4
        self.assertEqual(per, 318)
        self.assertEqual(-(-224 * 256 // (per >> 1)) * 2, 722)

    def test_command_waits(self):
        # C2/067B: a wait per SNES command (Fight 16, Magic 32, Morph/Revert 0, Jump 224 = its landing)
        import port
        table = bytes.fromhex(self.code(0xC2067B, 30))
        self.assertEqual((table[0], table[2], table[3], table[22]), (16, 32, 0, 224))
        atb = (open(os.path.join(PR, "plugin", "PrKit", "Ff6", "SnesAtb.cs"), encoding="utf-8").read() + open(os.path.join(PR, "plugin", "PrKit", "Ff6", "SnesAtbRules.cs"), encoding="utf-8").read())
        block = atb[atb.index("CommandWait = new"):]  # kit SnesAtbRules.CommandWait since 11-20
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

    def test_kit_rules_agree(self):
        """The kit's rules read from the vanilla ROM (tools/port_encounter_rates.py, what BNW's port exports
        for its own ROM) give WC's tables: the same PR sets off normal, and on every PR world-map cell that
        counts steps the same code from the zone and the ground's terrain group as WC's zone table with its
        zone 10 forest; the vanilla roll and party odds."""
        import port_encounter_rates as R
        import prdata as P
        r = R.rules(self.rom, self.rom, "vanilla", log=lambda *a: None)
        self.assertEqual({int(k): v for k, v in r["set_codes"].items()}, self.table("SetCode"))
        self.assertEqual((r["roll"], r["party_odds"]), ({"add": 0, "range": 256}, {"world": R.VANILLA_ODDS, "map": R.VANILLA_ODDS}))
        wob, forest = self.table("WobZoneCode"), int(re.search(r"Zone10Forest = (\d+);", self.src).group(1))
        areas = {int(a["encount_area_grid_number"]): a for a in P.Table("encount_area")}
        steps = {int(f["id"]): int(f["subtract_steps"]) for f in P.Table("foot_information")}
        cells, bad = 0, []
        for w, n in R.PR_WORLDS.items():
            d = R._map_dir(n)
            if not os.path.exists(os.path.join(d, "encount_grid.json")):
                self.skipTest("no extracted PR world maps")
            grid = json.load(open(os.path.join(d, "encount_grid.json"), encoding="utf-8"))["cells"]
            ground = json.load(open(os.path.join(d, "attribute.json"), encoding="utf-8"))["layers"][0]["data"]
            for i, c in enumerate(grid):
                ft, row = ground[i], areas.get(int(c))
                if row is None or not 1 <= ft <= 32 or not steps.get(ft):
                    continue
                s = int(row[f"foot{ft:02d}_monster_set"])
                if s <= 128:   # sets 1-128: no parties
                    continue
                z = (i // 256 // 32) * 8 + (i % 256) // 32
                g = int(r["foot_group"][str(w)].get(str(ft), 0))
                kit = (r["world_zones"][str(w)][z] >> (2 * (3 - g))) & 3
                wc = (0 if z == 10 and s == forest else wob.get(z, 0)) if w == 1 else 0
                cells += 1
                if kit != wc:
                    bad.append((w, z, ft, s, kit, wc))
        self.assertGreater(cells, 20000)
        self.assertEqual(bad[:10], [])

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
        test = wc_test_source()
        for r in (0xC0, 0x60, 0x180, 0x70, 0x40, 0x160, 0x200):
            self.assertIn(f"[0x{r:X}] = {mean(r):g}", test)


class WcHookPlaces(unittest.TestCase):
    """Where -eu and -gpm hook the game (2026-11-21). IsChangeEquipmentOnlyAccessory's machine code
    (RVA 0x4421B0) is shared with three NoARBattle lambdas: no Harmony patch on it, a vtable entry
    instead. Gil is paid from BattleAddExpPlug.GetGil (SaveGil), not Genelate's gilValue (the
    result screen's number): -gpm multiplies GetGil's answer, once. Magic points: -mpm in each
    StatusUpProvider.Execution (the esper learning), not the morph gauge."""

    @classmethod
    def setUpClass(cls):
        d = os.path.join(PR, "plugin", "WcPlugin")
        cls.code = {}
        for p in glob.glob(os.path.join(d, "*.cs")):
            with open(p, encoding="utf-8") as f:
                cls.code[os.path.basename(p)] = "".join(re.sub(r"//.*", "", line) for line in f)

    def test_no_detour_on_the_shared_lock(self):
        for name, src in self.code.items():
            for m in re.finditer(r"AccessTools\.Method\([^;]*\"IsChangeEquipmentOnlyAccessory\"[^;]*;", src):
                after = src[m.end():m.end() + 400]
                self.assertNotRegex(after.split("\n\n")[0], r"\.Patch\(", f"{name}: a Harmony patch on the shared code")
        rec = self.code["WcRecruits.cs"]
        self.assertIn("GetFunctionPointerForDelegate", rec)
        self.assertNotIn("CanOpenRestrictionMenuWindow", rec)

    def test_gil_multiplied_where_it_is_paid(self):
        rew = self.code["WcRewards.cs"]
        self.assertRegex(rew, r'AccessTools\.Method\(typeof\(BattleAddExpPlug\), "GetGil"\)')
        prefix = re.search(r"void Result_Prefix\(([^)]*)\)", rew)
        self.assertIsNotNone(prefix)
        self.assertNotIn("gilValue", prefix.group(1), "Genelate's gilValue is only the screen's: multiplying it too doubles nothing paid")
        self.assertIn('"reward_wallet"', wc_test_source())
        self.assertIn('"eu_equip"', wc_test_source())

    def test_magic_points_multiplied_where_espers_learn(self):
        """-mpm (2026-11-21): BattleAddExpPlug.GetAp has no callers; the magic points are
        BattleSkillUpInformation.MagicPoint, read by Genelate (the result's GetMp and the morph gauge)
        and by each sharer's StatusUpProvider.Execution (the esper learning). WC multiplies the
        learning's amount (at most 255) and gives the morph gauge that / -mpm."""
        rew = self.code["WcRewards.cs"]
        self.assertNotIn("GetAp", rew, "the dead GetAp hook")
        self.assertRegex(rew, r'AccessTools\.Method\(typeof\(StatusUpProvider\), "Execution"\)')
        self.assertRegex(rew, r"void Result_Prefix\([^)]*BattleSkillUpInformation battleSkillUpInformation\)")
        self.assertRegex(rew, r"void Learning_Prefix\(BattleSkillUpInformation battleSkillUpInformation, out int __state\)")
        self.assertRegex(rew, r"void Learning_Postfix\(BattleSkillUpInformation battleSkillUpInformation, int __state\)")
        self.assertIn("Math.Min(255L", rew)
        self.assertIn("GetResultMagicProficiency", rew)
        src = wc_test_source()
        self.assertIn('"reward_mp"', src)
        self.assertIn("Plan_reward_mp(name)", src)
        wc_mult = os.path.join(PR, "..", "mods", "worlds-collide", "battle", "multipliers.py")
        if not os.path.exists(wc_mult):
            return
        with open(wc_mult, encoding="utf-8") as f:
            wc = f.read()
        self.assertIn("multiply_max_255", wc)
        self.assertIn("divide gained morph time by magic points multiplier", wc)


if __name__ == "__main__":
    unittest.main()
