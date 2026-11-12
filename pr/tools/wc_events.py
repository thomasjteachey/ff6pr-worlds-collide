"""Worlds Collide's event changes as PR event scripts (static: the same for every seed).

Per-seed choices (start party, each check's reward) stay out of the scripts: a script calls
SysCall "WC:<name>" and the plugin (WcPlugin/WcEvents.cs) does the seed's part, reading the
active seed's wc_seed.json. These files ship in the WorldsCollide Magicite folder
(wc_live.write_baseline).

Changes so far (WC's own flags: ScenarioFlag4 0-131, unused by vanilla scripts; WC_GAME = 0):
 * New Game (sc_e_0001, the opening on the cliff): with a WC seed active the plugin sets up
   WC's start (the seed's party, gil/items/espers) and the game begins on the
   Blackjack's deck in the World of Balance, airship parked outside Narshe (WC event/start.py).
   Without a seed, or in the title's attract demo, the vanilla opening runs.
 * Tzen's esper seller (WC event/tzen.py; sc_e_0427 WoB, sc_e_0640 WoR, sc_map_20190): there
   from the start; the seed's reward for the seed's price in each world (WC:offer / WC:pay /
   WC:reward); buying it sets the check's flag and both vanilla "bought" flags.
 * Ancient Castle's stone queen (WC event/ancient_castle.py, sc_e_0890): the seed's reward instead
   of Raiden, if the gate character (Edgar) has been recruited (with -cg).
 * Gau's father's house (WC event/gau_father_house.py, sc_map_20090 / sc_e_0097): the NPC is
   there until the check is done (gate Shadow); talking gives the seed's reward.
 * Kohlingen's inn (WC event/kohlingen.py, sc_map_20131_1 / sc_e_0285): the NPC is there until
   the check is done (gate Setzer); talking gives the seed's reward (WoB; the PR has no WoR copy).
 * Recruit scenes as checks (`talk_reward`): talking gives the seed's reward at once if the gate
   is met (WC strips these cutscenes): Owzer's Mansion (sc_e_0775), the Zone Eater's Gogo
   (sc_e_0865), Mt. Zozo's Cyan (sc_e_0735), Umaro's cave (sc_e_0790; the carving's Midgardsormr
   is left out, `skip`).
 * Post-boss checks (`gate_at` on the scene that starts the fight, `reward_at` on the vanilla join
   or done flag after it): Esper Mountain (sc_e_0547 / _1), Figaro Castle WoR's engine room
   (sc_e_0672 / _1), Tzen WoR's collapsing house (sc_map_20330 / sc_e_0639). Narshe WoR's weapon
   shop (sc_e_0873): the smith gives the seed's first reward. With character gating the vanilla
   character of each scene has joined already when its gate is open, so the vanilla joins left
   in the scenes are harmless.
 * The Magitek Factory's first reward (WC: Ifrit / Shiva): either magicite pickup (sc_e_0399 /
   sc_e_0400) gives it and both disappear. Number 024's and the ending's rewards: not yet.
 * The airships' wheels (WC event/airship.py): the Blackjack's adds "Fly to the World of Ruin",
   the Falcon's "Fly to the World of Balance"; the plugin puts the other airship where this one
   is (WC:to_wor / WC:to_wob) and the party flies on in the other world. "After obtaining the
   Falcon" (ScenarioFlag2 97) and "after the collapse" (ScenarioFlag2 41: the merged WoB/WoR towns
   show their WoR state) are set in the WoR and cleared in the WoB, as WC does with IN_WOR /
   GOT_FALCON.
 * Tritoch (WC event/tritoch.py, sc_e_0780_1): the seed's reward after the battle.
 * Zozo (WC event/zozo.py, sc_map_30511_12 / sc_e_0303): only Ramuh at the top (gate Terra); his
   scene gives the seed's reward. (WC also randomizes the clock puzzle: not ported, the PR's clock
   and its hints stay vanilla, so they still agree.)
 * Story scenes outside the checks (story_scenes, NO_SCENE_LAYERS): not placed or skipped where
   they start (the Blackjack's engine room, Doma's poisoning, Esper Mountain's espers...); the
   Magitek Factory's end as WC's (magitek_tubes, magitek_escape). tools/wc_scene_audit.py lists
   the scenes and vanilla joins a WC game can still reach.

  .venv/Scripts/python tools/wc_events.py OUT_DIR     (writes the scripts, prints a summary)
"""
import copy
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import pr_script as S  # noqa: E402
import prdata as P  # noqa: E402

BLACKJACK, FALCON = 4, 5   # transportation ids
WOB_MAP, WOR_MAP = 1, 2    # Map_10010 / Map_10020
DECK_MAP = 119             # Map_20170, the Blackjack's deck (Falcon's: 268, Map_20390)
NARSHE_PARK = (84, 34)     # WC parks the airship here (SNES and PR world tiles agree)
WC_VAR = 6                 # map-local variable the plugin sets to 1 when it ran WC's start

# WC's own flags live in ScenarioFlag4 0-131, which no vanilla script sets or tests (saved with
# the game like every story flag)
WC_FLAGS = "ScenarioFlag4"
WC_GAME = 0                # this save is a Worlds Collide game (set by WC's start)

# PR story flags (tools/pr_flags.py) for WC's SNES event bits
GOT_FALCON = ("ScenarioFlag2", 97)   # "after obtaining the Falcon": WC sets IN_WOR + GOT_FALCON in the WoR
COLLAPSED = ("ScenarioFlag2", 41)    # "after the world's collapse": the PR's merged WoB/WoR towns (40
                                     # maps: Narshe, Figaro...) show their WoR state with it = IN_WOR

# each check's done flag (WC's FinishCheck): WC_FLAGS index, by WC event module
CHECK_FLAGS = {
    "tzen": 1,
    "ancient_castle": 2,
    "gau_father_house": 3,
    "kohlingen": 4,
    "tritoch": 5,
    "zozo": 6,
    "owzer_mansion": 7,
    "zone_eater": 8,
    "mt_zozo": 9,
    "umaro_cave": 10,
    "esper_mountain": 11,
    "figaro_castle_wor": 12,
    "collapsing_house": 13,
    "narshe_wor": 14,
    "magitek_factory": 15,      # its first reward (Ifrit/Shiva); 2nd, 3rd: Number 024, the end (not yet)
    "doma_wor": 16,             # Cyan wakes (WC's reward 1)
    "doma_wor_throne": 17,      # the throne (reward 2); the dream's Three Stooges (reward 3): not yet
    "mobliz_wor": 18,
    "doom_gaze": 19,
    "magitek_factory_3": 20,    # the factory's third reward (the tube espers' scene); 2nd = Number 024: not yet
    "lone_wolf": 21,            # Mog after the chase (reward 1)
    "lone_wolf_2": 22,          # the Gold Hairpin's place (reward 2)
    "daryl_tomb": 23,
    "baren_falls": 24,          # where Gau is met and named after the falls
    "south_figaro": 25,
    "figaro_castle_wob": 26,
    "mt_kolts": 27,
    "narshe_wor_2": 28,         # the smith's second reward (WC: a guard in the mines gives it; here: the smith)
    "magitek_factory_2": 29,    # after Number 024
    "doma_wor_3": 30,           # the dream's Three Stooges
    "veldt": 31,                # Gau's dried-meat scene
    "fanatics_tower": 32,       # Strago at the tower's foot (reward 1)
    "fanatics_tower_2": 33,     # the top's treasure (reward 2)
    "ebots_rock": 34,
    "burning_house": 35,
    "whelk": 36,
    # WC's 8 dragons (event/eight_dragons.py: one item each, in its reward order)
    "narshe_dragon": 37, "mt_zozo_dragon": 38, "opera_house_dragon": 39, "kefka_tower_dragon_g": 40,
    "kefka_tower_dragon_s": 41, "ancient_castle_dragon": 42, "phoenix_cave_dragon": 43, "fanatics_tower_dragon": 44,
    "auction_1": 45,            # WC's reward1 (10,000 gil in WC): the Zona Seeker's sale
    "auction_2": 46,            # WC's reward2 (20,000): the Golem's
    "kefka_tower": 47,          # after the Ultima Buster (WC: Atma, "Cell Beast")
    "floating_continent": 48,   # Shadow's join on arrival (reward 1)
    "floating_continent_2": 49, # after the Ultima Weapon (reward 2)
    "floating_continent_3": 50, # the escape, on the Blackjack's deck (reward 3)
    "veldt_cave_wor": 51,       # after the Behemoth King
    "lete_river": 52,           # after Ultros on the raft
    "sealed_gate": 53,          # instead of Terra rejoining at the gate
    "serpent_trench": 54,       # on arrival at Nikeah
    "phantom_train": 55,        # after the Phantom Train
    "south_figaro_cave_wob": 56,  # after the Tunnel Armor
    "doma_wob": 57,             # after the siege's Captain
    "phoenix_cave": 58,         # at Locke's chest in the end room
    "narshe_battle": 59,        # after Kefka at Narshe
    "opera_house_wob": 60,      # after Ultros at the opera
    "imperial_camp": 61,        # after Cyan's third fight
    "narshe_moogle_defense": 62,  # after the Marshal
}
MOOGLE_OFFERED = 130       # ScenarioFlag4: the moogle defense's scene was started on entering the mines
# WC's own check names (its Track menu, constants/objectives/condition_bits.py) for each ported check
TRACK_NAMES = {
    "tzen": "Tzen Thief", "ancient_castle": "Ancient Castle", "gau_father_house": "Gau's Father's House",
    "kohlingen": "Kohlingen Cafe", "tritoch": "Tritoch Cliff", "zozo": "Zozo Tower", "owzer_mansion": "Owzer's Mansion",
    "zone_eater": "Zone Eater", "mt_zozo": "Mt. Zozo", "umaro_cave": "Umaro's Cave", "esper_mountain": "Esper Mountain",
    "figaro_castle_wor": "Figaro Castle Engine", "collapsing_house": "Collapsing House",
    "narshe_wor": "Narshe Weapon Shop", "narshe_wor_2": "Narshe Weapon Shop Mines",
    "magitek_factory": "Magitek Factory Trash", "magitek_factory_2": "Magitek Factory Guard",
    "magitek_factory_3": "Magitek Factory Finish", "doma_wor": "Doma Dream Awaken", "doma_wor_throne": "Doma Dream Throne",
    "doma_wor_3": "Doma Dream Door", "mobliz_wor": "Mobliz Attack", "doom_gaze": "Search The Skies",
    "lone_wolf": "Lone Wolf Chase", "lone_wolf_2": "Lone Wolf Moogle Room", "daryl_tomb": "Daryl's Tomb",
    "baren_falls": "Baren Falls", "south_figaro": "South Figaro Prisoner", "figaro_castle_wob": "Figaro Castle Throne",
    "mt_kolts": "Mt. Kolts", "veldt": "Veldt", "fanatics_tower": "Fanatic's Tower Follower",
    "fanatics_tower_2": "Fanatic's Tower Leader", "ebots_rock": "Ebot's Rock", "burning_house": "Burning House",
    "whelk": "Whelk Gate",
    "narshe_dragon": "Narshe Dragon", "mt_zozo_dragon": "Mt. Zozo Dragon", "opera_house_dragon": "Opera House Dragon",
    "kefka_tower_dragon_g": "Kefka's Tower Dragon G", "kefka_tower_dragon_s": "Kefka's Tower Dragon S",
    "ancient_castle_dragon": "Ancient Castle Dragon", "phoenix_cave_dragon": "Phoenix Cave Dragon",
    "fanatics_tower_dragon": "Fanatic's Tower Dragon",
    "auction_1": "Auction 1", "auction_2": "Auction 2", "kefka_tower": "Kefka's Tower Cell Beast",
    "floating_continent": "Floating Cont. Arrive", "floating_continent_2": "Floating Cont. Beast",
    "floating_continent_3": "Floating Cont. Escape", "veldt_cave_wor": "Veldt Cave", "lete_river": "Lete River",
    "sealed_gate": "Sealed Gate", "serpent_trench": "Serpent Trench", "phantom_train": "Phantom Train",
    "south_figaro_cave_wob": "South Figaro Cave", "doma_wob": "Doma Siege", "phoenix_cave": "Phoenix Cave",
    "narshe_battle": "Narshe Battle", "opera_house_wob": "Opera House Disruption", "imperial_camp": "Imperial Camp",
    "narshe_moogle_defense": "Narshe Moogle Defense",
}
RECRUITED = 100            # + character status id (1-14): joined in this WC game (the plugin sets these)
SCRATCH = 131              # the plugin's answer to WC:gate (1 = the check may be done)
PICK_PARTY = 99            # the plugin's answer to WC:reward: 1 = a character joined (the party pick follows)
REWARD_LINE = 129          # the plugin's: a reward given in a battle; the battle script shows its line
PARTY_MENU = "\u30d1\u30fc\u30c6\u30a3\u7de8\u6210\uff1a\uff11\u30d1\u30fc\u30c6\u30a3"   # the PR's party select, one party

# story flags WC's start sets (its event modules' init_event_bits), as PR flags
START_FLAGS = [
    ("ScenarioFlag2", 172),   # GOT_ODIN + no Odin statue (ancient_castle.py): the PR's "got Odin"
    ("ScenarioFlag1", 159),   # the opera's stage ready (its trigger: the performance, WC's "Perform In
    ("ScenarioFlag1", 166),   # Opera" quest, opera_performance) + the letter read (the disruption check)
    # WC's start-of-game state (event/start.py and each event's init_event_bits), the PR flags
    # that play the same role (research 2026-11-05; flags that start a story sequence or close a
    # check's place left out: SF1 12-14, 64, 136-138, 178, 194, 207, 225; SF2 2, 19, 28):
    ("ScenarioFlag1", 7),     # no save point tutorial
    ("ScenarioFlag1", 8),     # Narshe's mine door open (Biggs/Wedge's scene done)
    ("ScenarioFlag1", 10), ("ScenarioFlag1", 11),   # Narshe's opening over (no raid battles, no frozen esper scene)
    ("ScenarioFlag1", 68), ("ScenarioFlag1", 69),   # Terra's scenario over (Arvis' house, the cave's soldier)
    ("ScenarioFlag1", 26), ("ScenarioFlag1", 98),   # South Figaro cave's Figaro side open, no spring line
    ("ScenarioFlag1", 27), ("ScenarioFlag1", 58), ("ScenarioFlag1", 61),   # South Figaro: Shadow, Celes found, clock
    ("ScenarioFlag1", 42), ("ScenarioFlag1", 47),   # Banon met, the hideout open: the Lete River raft reachable
    ("ScenarioFlag1", 135),   # Sabin's scenario over (no mountain on the walk to Nikeah)
    ("ScenarioFlag1", 158),   # the opera's overture seen: the impresario's disruption trigger; no Shadow leaving at Narshe
    ("ScenarioFlag1", 201),   # no Setzer coin toss in the cabin (it grounds the Blackjack)
    ("ScenarioFlag1", 212),   # the Lone Wolf chase on Narshe's cliffs
    ("ScenarioFlag3", 34),    # no free Genji Glove
    ("ScenarioFlag3", 36),    # no chocobo tutorial
    ("ScenarioFlag2", 1), ("ScenarioFlag2", 9),     # Gestahl's dinner over: Albrook's port, Tzen, Maranda freed; no Leo at Albrook
    ("ScenarioFlag2", 4),     # the Imperial Base's armory door open
    ("ScenarioFlag2", 23),    # Esper Mountain's statue scene seen
    ("ScenarioFlag2", 209), ("ScenarioFlag2", 210), ("ScenarioFlag2", 211), ("ScenarioFlag2", 212),   # Shadow's dreams
    ("ScenarioFlag2", 60), ("ScenarioFlag2", 61), ("ScenarioFlag2", 62), ("ScenarioFlag2", 63), ("ScenarioFlag2", 64),
    ("ScenarioFlag2", 98), ("ScenarioFlag2", 99),   # Mobliz WoR up to the last Phunbaba
    ("ScenarioFlag2", 201), ("ScenarioFlag2", 202), # Ebot's Rock open
    ("ScenarioFlag2", 171),   # the Ancient Castle found (no flashback at the entrance)
    ("ScenarioFlag2", 137),   # the moogle room in the WoR
    ("ScenarioFlag2", 103),   # Rust-Rid for sale in Zozo
    # Figaro Castle in the WoR (WC event/figaro_castle_wor.py, nikeah.py: the robbers' boat boarded,
    # the thieves at the cave's turtle): Gerad met at Nikeah, the ferry, South Figaro, the cave cleared
    ("ScenarioFlag2", 72), ("ScenarioFlag2", 73), ("ScenarioFlag2", 74), ("ScenarioFlag2", 75),
]

# text the scripts show (story_mes_en; empty speaker in story_cha_en). WC_REWARD is set by
# the plugin before each reward line (WC:reward).
MESSAGES = {
    "WC_HELM_WOR": "(Fly to the World of Ruin.)",
    "WC_HELM_WOB": "(Fly to the World of Balance.)",
    "WC_REWARD": "<CT>Obtained a reward!",
    "WC_MOOGLE_ASK": "Kupo! The Empire is attacking the mines! Will you help the moogles?",
    "WC_MOOGLE_YES": "(Defend the mines.)",
    "WC_MOOGLE_NO": "(Not now.)",
    "WC_KT_ASK": "The way to Kefka is open. Go straight to him?",
    "WC_KT_YES": "(Straight to Kefka.)",
    "WC_KT_NO": "(Climb the tower.)",
    "WC_UMARO_CLIFF": "There's an opening in the cliff.",
}
NOT_ENOUGH_GIL = "S9999_999_a_12"


def flag(f, i):
    return S.op("SetFlag", i=[i], s=[f])


def reset(f, i):
    return S.op("ResetFlag", i=[i], s=[f])


def if_flag(f, i, value, target):
    return S.op("Branch", i=[i, value, target], s=[f, EQ, "imm"])


def opening():
    """sc_e_0001: its first SysCall (the attract-demo flag) moves into an added block that runs
    WC's start when a seed is active (else back to the vanilla opening)."""
    sc = S.load("sc_e_0001")
    ms = sc["Mnemonics"]
    demo = ms[1]["operands"]["sValues"][0]
    assert ms[1]["mnemonic"] == "SysCall" and demo == ADVERTISE_DEMO, "opening changed"
    S.hook(sc, 1, "wc_start", [
        S.op("SysCall", s=[demo]),
        # WC:start decides: a seed is active and New Game was picked on the title (the vanilla
        # script's map-local 7 is 1 for the title's attract demo, 0 for a New Game; an earlier
        # version of this hook had it backwards and skipped WC's start on every real New Game)
        S.op("SysCall", s=["WC:start"]),
        S.op("Branch", i=[WC_VAR, 0, 1], s=["MapLocalVariable", EQ, "imm"]),  # no seed: vanilla from index 2
        flag(WC_FLAGS, WC_GAME),
    ] + [flag(*f) for f in START_FLAGS] + [
        S.op("SysCall", s=[LEAVE + TERRA]),          # the opening's party (Terra, Biggs, Wedge) leaves
        S.op("SysCall", s=[LEAVE + BIGGS]),
        S.op("SysCall", s=[LEAVE + WEDGE]),
        S.op("SysCall", s=["WC:party"]),              # the seed's starting characters join
        S.op("SetVehicle", i=[BLACKJACK, WOB_MAP, NARSHE_PARK[0], NARSHE_PARK[1], 0, 0]),
        S.op("ChangeMap", i=[DECK_MAP, 1, 0, 0, 1, 0], s=["sc_e_BJinboard_1"]),
        S.op("Exit"),
    ])
    return sc


EQ = "\uff1d"
ADVERTISE_DEMO = "\u30a2\u30c9\u30d0\u30bf\u30a4\u30ba\u30c7\u30e2\u30d5\u30e9\u30b0\u3092\u8a2d\u5b9a"
LEAVE = "\u30e1\u30f3\u30d0\u30fc\u96e2\u8131\uff1a"   # "member leaves: "
TERRA, BIGGS, WEDGE = "\u30c6\u30a3\u30ca", "\u30d3\u30c3\u30af\u30b9", "\u30a6\u30a7\u30c3\u30b8"
GPS_TO_PLAYER = "GPS\u306e\u5bfe\u8c61\u3092\u30d7\u30ec\u30a4\u30e4\u30fc\u306b"


def selection(choice, target):
    return S.op("Branch", i=[0, choice, target], s=["MessageSelection", EQ, "imm"])


def wheel(sc, at, lift_off, nothing, pose, choice_key, call, world_flags, to_map, arrive, extra=None):
    """A deck's wheel: the vanilla code at `at` (just after the label 通常時) moves into a block
    that, in a WC game, offers (Take the wheel.) / <choice_key> / (Never mind.). extra = (choice
    key, jump target, gate module, done check): one more choice first while the gate is met and
    the check isn't done (the Floating Continent)."""
    first = []
    if extra:
        key, target, module, done = extra
        first = [
            if_flag(WC_FLAGS, CHECK_FLAGS[done], 1, "@plain"),
        ] + gate(module, "@plain") + [
            S.op("Select", s=[key, "E0412_00_119_c_01", choice_key, "E0412_00_119_c_02"]),
            selection(0, target),
            selection(1, lift_off),
            selection(2, "@switch"),
            S.op("Jump", i=[nothing]),
            S.label("plain"),
        ]
    S.hook(sc, at, "wc_helm", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),                         # not a WC game: vanilla
    ] + first + [
        S.op("Select", s=["E0412_00_119_c_01", choice_key, "E0412_00_119_c_02"]),
        selection(0, lift_off),                                              # take the wheel
        selection(1, "@switch"),
        S.op("Jump", i=[nothing]),                                           # never mind
        S.label("switch"),
        S.op("Call", i=[pose]),                                              # the deck's lift-off pose
        S.op("FadeOut", i=[0, 0, 0, 255], r=[0.7]),
        S.op("SysCall", s=[call]),                                           # the other airship goes here
    ] + world_flags + [
        S.op("SysCall", s=[GPS_TO_PLAYER]),
        S.op("ChangeMap", i=[to_map, 1, 0, 0, 1, 0], s=[arrive]),
        S.op("Exit"),
    ])


def blackjack_helm():
    """The Blackjack's wheel (sc_e_BJoutboard, WoB): (Fly to the World of Ruin.) (WC
    event/airship.py: Lift-off / World of Ruin / Not just yet), and (Head to the Floating
    Continent.) once Shadow is found (WC event/floating_continent.py), any party size (vanilla:
    exactly three, [86])."""
    sc = S.load("sc_e_BJoutboard")
    ms = sc["Mnemonics"]
    assert [ms[i]["label"] for i in (6, 16, 111, 113)] == ["\u901a\u5e38\u6642", "\u767a\u9032\u3059\u308b",
                                                           "\u306a\u306b\u3082\u3057\u306a\u3044",
                                                           "\u767a\u9032\u3059\u308b\u30d1\u30da\u30c3\u30c8"], "Blackjack wheel changed"
    assert ms[22]["label"] == "\u9b54\u5927\u9678\u3078\u5411\u304b\u3046" and ms[86]["mnemonic"] == "BranchByNumOfMember", \
        "Blackjack wheel's Floating Continent path changed"
    wheel(sc, 7, lift_off=16, nothing=111, pose=113, choice_key="WC_HELM_WOR", call="WC:to_wor",
          world_flags=[flag(*GOT_FALCON), flag(*COLLAPSED)], to_map=WOR_MAP, arrive="sc_e_FAoutboard_1",
          extra=("E0559_00_119_c_01", 22, "floating_continent", "floating_continent_3"))
    S.hook(sc, 86, "wc_any_party", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Jump", i=[94]),
    ])
    # WC enters the trip after Setzer's "Let's find a way onto that thing!" (its wheel jumps to
    # CA/581A, past dialog $084D) and removes "Let's form a party of three!" with the 3-member
    # check (CA/582A-5839): the party menu takes up to four and neither line shows
    assert [ms[i]["operands"]["sValues"][0] for i in (24, 25)] == ["E0560_00_119_a_01", "E0560_00_119_a_02"], \
        "Floating Continent lines moved"
    wc_jump(sc, 24, 25)
    return sc


def falcon_helm():
    """The Falcon's wheel (sc_e_FAoutboard, WoR): (Fly to the World of Balance.). Doom Gaze's
    "Search the skies" comes with that check."""
    sc = S.load("sc_e_FAoutboard")
    ms = sc["Mnemonics"]
    assert [ms[i]["label"] for i in (4, 7, 12, 14)] == ["\u901a\u5e38\u6642", "\u767a\u9032\u3059\u308b",
                                                        "\u4f55\u3082\u3057\u306a\u3044", "hand_up"], "Falcon wheel changed"
    wheel(sc, 5, lift_off=7, nothing=12, pose=14, choice_key="WC_HELM_WOB", call="WC:to_wob",
          world_flags=[reset(*GOT_FALCON), reset(*COLLAPSED)], to_map=WOB_MAP, arrive="sc_e_BJoutboard_1")
    return sc


_picks = 0


def pick_party():
    """WC's RecruitAndSelectParty: after a character reward (WC:reward set PICK_PARTY) the party
    select screen, as the PR's party-switch points open it (black, the field's figures hidden),
    then the leader redrawn where the party stands (WC:party_refresh: the PR's own points reload
    the map instead, which would cut the check's scene short)."""
    global _picks
    _picks += 1
    done = f"wc_picked_{_picks}"
    return [
        if_flag(WC_FLAGS, PICK_PARTY, 0, f"@{done}"),
        S.op("ColorFade", i=[0, 0, 0, 255]),
        S.op("HideMob"),
        S.op("SysCall", s=[PARTY_MENU]),
        S.op("RestoreMob"),
        S.op("ColorFade", i=[0, 0, 0, 0]),
        S.op("SysCall", s=["WC:party_refresh"]),
        S.label(done),
    ]


def give(module, flags=(), n=0, check=None, se=True):
    """The check's reward line: WC:reward (the seed's n-th reward of that WC event), the
    "Obtained ..." text, the check's done flag (CHECK_FLAGS[check or module]) and `flags`; a
    character then gets WC's party pick (pick_party). se=False: no fanfare (PlaySE keeps its
    handle in a script-local: not in scripts that count with one, sc_e_0975)."""
    return [
        reset(WC_FLAGS, PICK_PARTY),
        S.op("SysCall", s=[f"WC:reward {module} {n}"]),
    ] + ([S.op("PlaySE", i=[105, 1])] if se else []) + [
        S.op("Msg", s=["WC_REWARD"]),
        flag(WC_FLAGS, CHECK_FLAGS[check or module]),
    ] + [flag(*f) for f in flags] + pick_party()


TZEN_BOUGHT = [("ScenarioFlag1", 206), ("ScenarioFlag2", 57)]   # vanilla: bought in the WoB / WoR


def tzen_seller(name, at, world, offer_key, after):
    """Tzen's esper seller (WC event/tzen.py): the seed's reward (esper or item) for the seed's
    price in each world (WC randomizes both); buying in one world sells out the other."""
    sc = S.load(name)
    ms = sc["Mnemonics"]
    assert ms[at]["mnemonic"] == "Msg" and ms[at]["operands"]["sValues"][0] == offer_key, f"{name} changed"
    S.hook(sc, at, "wc_tzen", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["tzen"], 1, after),               # sold: the vanilla after-line
        S.op("SysCall", s=[f"WC:offer tzen {world} {offer_key}"]),
        S.op("Msg", s=[offer_key]),
        S.op("Select", i=[0, 0, 1]),
        selection(1, "@no"),
        S.op("SysCall", s=[f"WC:pay tzen {world}"]),
        S.op("Branch", i=[0, 1, "@paid"], s=["MapLocalVariable", EQ, "imm"]),
        S.op("Msg", s=[NOT_ENOUGH_GIL]),
        S.op("Exit"),
        S.label("paid"),
    ] + give("tzen", TZEN_BOUGHT) + [
        S.label("no"),
        S.op("Exit"),
    ])
    return sc


def tzen_wob():
    return tzen_seller("sc_e_0427", 2, 0, "E0427_00_132_a_01", after=16)


def tzen_wor():
    return tzen_seller("sc_e_0640", 3, 1, "E0427_01_132_a_01", after=20)


def gate(module, closed):
    """WC's character gate (with -cg): WC:gate sets SCRATCH to 1 when the check's gating
    character has been recruited (or the seed has no gating); else go to `closed`."""
    return [
        S.op("SysCall", s=[f"WC:gate {module}"]),
        if_flag(WC_FLAGS, SCRATCH, 0, closed),
    ]


def ancient_castle():
    """The stone queen (sc_e_0890, Ancient Castle; WC event/ancient_castle.py, gate Edgar): the
    seed's reward instead of Odin becoming Raiden (WC players never get Odin: GOT_ODIN at the
    start = the PR's ScenarioFlag2 172, which also makes the queen's event appear)."""
    sc = S.load("sc_e_0890")
    ms = sc["Mnemonics"]
    assert ms[18]["mnemonic"] == "Msg" and ms[18]["operands"]["sValues"][0] == "E0890_00_519_a_04", "queen changed"
    S.hook(sc, 18, "wc_queen", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + gate("ancient_castle", "@closed") + give("ancient_castle", [("ScenarioFlag2", 176)]) + [
        S.op("Exit"),
        S.label("closed"),                    # gate closed: the tear, nothing more (come back later)
        S.op("Exit"),
    ])
    return sc


QUEEN, QUEEN_ART = 8, 47      # the queen's statue in her room (Map_30811_7 entity 8, mapobject 47)
STATUE_TINT = {"r": 0.6, "g": 0.6, "b": 0.65, "a": 1}


def ancient_castle_room():
    """The queen's room (sc_map_30811_7, map 519): on entering, the map script cues its own
    'stone' on the statue, one frame (Stone_00) of the queen's sprite sheet. A seed whose reward
    here is a character shows the statue as that character (tools/wc_npcs.py, as WC does), whose
    sheet has no Stone_00, and the field never faded in: the room stayed black (the user's Deck,
    2026-11-12, the statue as Gogo). In a WC game the vanilla stone is only cued while the statue
    is the queen's art (WC:is_asset); another sprite stands still in its own first frame, greyed
    (WC shows the character in the statue's grey palette)."""
    sc = S.load("sc_map_30811_7")
    ms = sc["Mnemonics"]
    assert ms[9]["mnemonic"] == "Call" and ms[9]["operands"]["iValues"][0] == 11 and \
        ms[12]["operands"]["iValues"][:2] == [0, QUEEN] and ms[16]["operands"]["sValues"][0] == "stone", "queen's room changed"
    assert [a["name"] for a in sc["Animations"]] == ["stone"], "queen's room animations changed"
    sc["Animations"].append({"name": "wc_stone", "frames": [{
        "sprite_name": "Default_00", "offsetx": 0, "offsety": 0, "alpha": 1, "rgb": dict(STATUE_TINT), "flip": False}],
        "play_speed": 0, "play_mode": 0, "is_reverse": False, "next_anim_name": ""})
    S.hook(sc, 9, "wc_statue", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("SysCall", s=[f"WC:is_asset {QUEEN} {QUEEN_ART}"]),
        if_flag(WC_FLAGS, SCRATCH, 1, "@vanilla"),    # still the queen: her stone
        S.op("Call", i=["@wc_statue_pose"]),
        S.op("Exit"),
    ] + puppets("wc_statue_pose", (QUEEN,), lambda k: [S.op("CueAnim", s=["wc_stone"], kind=2)]))
    return sc


def gau_father_house_map():
    """Gau's father's house (Map_20090; WC event/gau_father_house.py, gate Shadow): the NPC
    (the PR's Shadow) is there in a WC game until the check is done, if its gate is met; the
    vanilla conditions (ScenarioFlag1 71 Shadow joined here, 123 Shadow left at Baren Falls)
    don't apply."""
    sc = S.load("sc_map_20090")
    ms = sc["Mnemonics"]
    assert ms[8]["mnemonic"] == "Branch" and ms[13]["label"] and ms[11]["operands"]["sValues"][0] == "ev_e_0097", "house map changed"
    S.hook(sc, 8, "wc_house", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["gau_father_house"], 1, 13),     # done: nobody there
    ] + gate("gau_father_house", 13) + [
        S.op("PlayBGM", i=[12, 1]),
        S.op("SetEntities", s=["ev_e_0097"]),
        S.op("Exit"),
    ])
    return sc


def gau_father_house():
    """Talking to him (sc_e_0097): the seed's reward, then he and Interceptor are gone (the
    vanilla join's fade and hide)."""
    sc = S.load("sc_e_0097")
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "Branch" and ms[110]["label"] == "1", "Shadow's scene changed"
    # as the vanilla join: fade out and the scene's puppets (he and Interceptor hidden) first; a
    # join as the script's first act hung the game (headless test, 2026-11-05)
    S.hook(sc, 1, "wc_house", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("FadeOut", i=[0, 0, 0, 255], r=[0.7]),
        S.op("Call", i=[110]),
        reset(WC_FLAGS, PICK_PARTY),
        S.op("SysCall", s=["WC:reward gau_father_house"]),
        S.op("FadeIn", i=[0, 0, 0, 255], r=[0.7]),
        S.op("PlaySE", i=[105, 1]),
        S.op("Msg", s=["WC_REWARD"]),
        flag(WC_FLAGS, CHECK_FLAGS["gau_father_house"]),
        flag("ScenarioFlag1", 71),
    ] + pick_party() + [
        S.op("Exit"),
    ])
    return sc


def kohlingen_map():
    """Kohlingen's inn (WoB, Map_20131_1 = PR map 98; WC event/kohlingen.py, gate Setzer): the
    NPC (the PR's Shadow, hired there for 3000 gil) is there in a WC game until the check is
    done, if its gate is met (vanilla: between two story points, until hired)."""
    sc = S.load("sc_map_20131_1")
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "Branch" and ms[7]["label"] and ms[5]["operands"]["sValues"][0] == "ev_e_0285", "Kohlingen inn changed"
    S.hook(sc, 1, "wc_inn", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["kohlingen"], 1, 7),
    ] + gate("kohlingen", 7) + [
        S.op("PlayBGM", i=[12, 1]),
        S.op("SetEntities", s=["ev_e_0285"]),
        S.op("Exit"),
    ])
    return sc


def kohlingen():
    """Talking to him (sc_e_0285): the seed's reward for free (WC), as the vanilla hire ends:
    fade, the scene's puppets, then the reward; ScenarioFlag1 147 = hired."""
    sc = S.load("sc_e_0285")
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "PlayBGM" and ms[184]["label"] == "Puppet_0002", "Kohlingen scene changed"
    S.hook(sc, 1, "wc_inn", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("FadeOut", i=[0, 0, 0, 255], r=[0.7]),
        S.op("Call", i=[184]),
        reset(WC_FLAGS, PICK_PARTY),
        S.op("SysCall", s=["WC:reward kohlingen"]),
        S.op("FadeIn", i=[0, 0, 0, 255], r=[0.7]),
        S.op("PlaySE", i=[105, 1]),
        S.op("Msg", s=["WC_REWARD"]),
        flag(WC_FLAGS, CHECK_FLAGS["kohlingen"]),
        flag("ScenarioFlag1", 147),
    ] + pick_party() + [
        S.op("Exit"),
    ])
    return sc


def tritoch():
    """Tritoch (WC event/tritoch.py; the PR's Valigarmanda on Narshe's cliffs, Map_20040 = PR map
    20, in the WoR): after the battle (sc_e_0780_1), the seed's reward (esper or item) instead of
    Valigarmanda's magicite; the rest of the scene as vanilla. The cliffs show him in the WoR
    already (ScenarioFlag2 97 set there, 139 = done)."""
    sc = S.load("sc_e_0780_1")
    ms = sc["Mnemonics"]
    assert ms[16]["operands"]["sValues"][0] == "E0780_00_020_a_02" and ms[25]["mnemonic"] == "GetItem" \
        and ms[26]["mnemonic"] == "SetFlag", "Tritoch scene changed"
    S.hook(sc, 16, "wc_tritoch", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + give("tritoch") + S.copy_ops(sc, 17, 25) + S.copy_ops(sc, 26, 28))
    return sc


def zozo_map():
    """The top of Zozo (Map_30511_12 = PR map 395; WC event/zozo.py, gate Terra): in a WC game
    only Ramuh is there (WC clears ESPER_TERRA_ZOZO and gives no extra magicite), until the check
    is done, if the gate is met. Vanilla: esper-Terra on the bed, Ramuh after ScenarioFlag1 149,
    three magicite pickups (Siren, Kirin, Cait Sith: sc_e_0304-0306), the party around."""
    sc = S.load("sc_map_30511_12")
    ms = sc["Mnemonics"]
    assert ms[2]["mnemonic"] == "Branch" and ms[9]["operands"]["sValues"][0] == "ev_e_0303" and ms[47]["label"], "Zozo top changed"
    S.hook(sc, 2, "wc_zozo", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["zozo"], 1, 47),
    ] + gate("zozo", 47) + [
        S.op("SetEntities", s=["ev_e_0303"]),
        S.op("Call", i=[56]),                       # Ramuh's pose
        S.op("Exit"),
    ])
    return sc


def zozo():
    """Ramuh's scene (sc_e_0303): his talk as vanilla, then the seed's reward instead of his
    magicite (ScenarioFlag1 150 = taken); the room's script runs again (ChangeScript) and he's gone."""
    sc = S.load("sc_e_0303")
    ms = sc["Mnemonics"]
    assert ms[9]["operands"]["sValues"][0] == "E0303_00_395_a_03" and ms[10]["mnemonic"] == "GetItem" \
        and ms[12]["mnemonic"] == "ChangeScript", "Ramuh's scene changed"
    S.hook(sc, 9, "wc_zozo", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + give("zozo", [("ScenarioFlag1", 150)]) + S.copy_ops(sc, 12, 14))
    return sc


def talk_reward(name, at, module, done_flags, expect=None, hide=(), after_state=False):
    """A recruit scene as a WC check: in a WC game, talking gives the seed's reward right away
    (WC strips these cutscenes) if the check's gate is met, and sets the scene's vanilla done
    flags so the room's map script leaves its NPC out from the next load on; gate closed:
    nothing happens (come back later). `at`: the scene's first instruction.
    Check done: nothing either. The NPC (or the trigger) is still in the room until it reloads,
    and most of these scenes' vanilla code is the recruit itself: talking to the Zone Eater's
    stand-in again after its Minerva Bustier ran Gogo's naming and join (the user's playtest,
    2026-11-12). after_state: the vanilla code branches on its own done flag (Mt. Zozo's door
    into Cyan's room) and runs once the check is done.
    hide: the stand-in's entity ids, hidden after the reward (and its party pick) as WC
    deletes the NPC (Gogo, Umaro)."""
    sc = S.load(name)
    ms = sc["Mnemonics"]
    if expect:
        assert ms[at]["mnemonic"] == expect, f"{name}[{at}] is {ms[at]['mnemonic']}, expected {expect}"
    gone = [S.op("Call", i=["@wc_hide_npc"])] if hide else []
    S.hook(sc, at, "wc_" + module, [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS[module], 1, "@vanilla" if after_state else "@closed"),
    ] + gate(module, "@closed") + give(module, done_flags) + gone + [
        S.op("Exit"),
        S.label("closed"),
        S.op("Exit"),
    ] + (puppets("wc_hide_npc", hide, lambda k: [S.op("Hide", kind=2)]) if hide else []))
    return sc


def skip(name, at, expect, why):
    """In a WC game, skip one vanilla instruction (an esper WC puts in its reward pool...)."""
    sc = S.load(name)
    ms = sc["Mnemonics"]
    got = ms[at]["mnemonic"] + " " + " ".join(x for x in ms[at]["operands"].get("sValues") or [] if x)
    assert got.startswith(expect), f"{name}[{at}] is {got}, expected {expect} ({why})"
    S.hook(sc, at, "wc_skip", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Jump", i=[at]),                     # goes on at at + 1
    ])
    return sc


def gate_at(sc, at, module, check=None):
    """In a WC game, the scene at `at` only runs once the check's gate is met (closed: Exit,
    nothing used up) and while the check isn't done. (`at` moves past labelled Nops: the first
    instruction that runs.) check: the done flag of a module's later reward (CHECK_FLAGS key):
    the module's own is its first reward's, so a second or third reward's scene keyed to it
    closed once the first was taken (the Magitek Factory's Number 024 and tubes, the Lone
    Wolf's hairpin)."""
    ms = sc["Mnemonics"]
    while ms[at]["mnemonic"] == "Nop":
        at += 1
    S.hook(sc, at, "wc_gate", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS[check or module], 1, "@closed"),
    ] + gate(module, "@closed") + [
        S.op("Jump", i=["@vanilla"]),
        S.label("closed"),
        S.op("Exit"),
    ])


def fade_in():
    return S.op("FadeIn", i=[0, 0, 0, 255], r=[0.7])


def fade_out():
    return S.op("FadeOut", i=[0, 0, 0, 255], r=[0.7])


def reward_at(sc, at, module, then=None, n=0, check=None, dark=False):
    """In a WC game, the vanilla instruction at `at` (the vanilla character's join, a done flag...)
    gives way to the seed's reward; then the vanilla code goes on at `then` (default at + 1:
    the instruction is replaced) or, with then="keep", the instruction runs too.
    dark: the screen is faded out at `at` (vanilla joins characters in the dark, between scenes):
    faded in for the reward's line and out again after, so vanilla goes on as it was. A line
    opened on black waits for a press nobody can tell it wants (the user's playtest,
    2026-11-06: the Imperial Camp; test_wc test_no_reward_line_on_a_black_screen)."""
    after = [S.op("Jump", i=["@vanilla"])] if then == "keep" else [S.op("Jump", i=[(then or at + 1) - 1])]
    reward = give(module, n=n, check=check)
    if dark:
        reward = [fade_in()] + reward + [fade_out()]
    S.hook(sc, at, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + reward + after)


def esper_mountain_start():
    """Esper Mountain (WC event/esper_mountain.py, gate Relm; map 425): the Ultros statue scene
    (sc_e_0547) only with the gate met. (Relm's joins in it are harmless: the gate means she
    has joined already.)"""
    sc = S.load("sc_e_0547")
    gate_at(sc, 1, "esper_mountain")
    return sc


def esper_mountain():
    """After Ultros (sc_e_0547_1): WC replaces the whole Relm/Thamasa continuation with the
    seed's reward.  The SNES replacement owns BF/0D9-BF/167, sets the defeated flag, fades the
    field in, gives the reward and returns; none of the vanilla post-battle scene runs."""
    sc = S.load("sc_e_0547_1")
    ms = sc["Mnemonics"]
    assert ms[18]["operands"]["sValues"][0].endswith("リルム") and ms[17]["operands"]["iValues"][0] == 24, "Esper Mountain changed"
    S.hook(sc, 1, "wc_finish", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        flag("ScenarioFlag2", 24),
        fade_in(),
    ] + give("esper_mountain") + [
        S.op("Exit"),
    ])
    return sc


def figaro_castle_wor_start():
    """Figaro Castle in the WoR (WC event/figaro_castle_wor.py, gate Edgar; map 440): the engine
    room scene (sc_e_0672: Edgar joins, the Tentacles) only with the gate met."""
    sc = S.load("sc_e_0672")
    ms = sc["Mnemonics"]
    assert ms[2]["mnemonic"] == "Call" and ms[28]["operands"]["sValues"][0].endswith("エドガー") and \
        ms[33]["mnemonic"] == "EncountBoss" and ms[33]["operands"]["sValues"][0] == "sc_e_0672_1", "Figaro engine room changed"
    gate_at(sc, 1, "figaro_castle_wor")
    # WC (figaro_castle_wor.py engine_room_mod: "split up party", the thieves' lines, "recruit
    # edgar") goes from Gerad to the fight: no thieves' plan, no reveal, no Edgar join/auto-equip
    # (the user's Deck, 2026-11-12: "there's still a cutscene with edgar in disguise")
    S.hook(sc, 2, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"), S.op("Jump", i=[32])])
    return sc


FIGARO_ENGINE = (440, 1)   # the engine room from its entrance (Map_30621_6 point 1)


def figaro_castle_wor():
    """After the Tentacles (sc_e_0672_1): the seed's reward just before the vanilla done flag
    (ScenarioFlag2 79). In a WC game the scene before it (the thieves run in for the treasure,
    Edgar drops his disguise, ~440 instructions; WC: "scenes after tentacles" -> the reward)
    is cut: the reward, the done flag, then the room reloads in its after state (no thieves; the
    scene had hidden the party for good)."""
    sc = S.load("sc_e_0672_1")
    ms = sc["Mnemonics"]
    assert ms[65]["mnemonic"] == "SetFlag" and ms[65]["operands"]["iValues"][0] == 79, "Figaro WoR changed"
    assert ms[2]["mnemonic"] == "HidePlayer" and ms[10]["mnemonic"] == "FadeIn", "after the Tentacles changed"
    S.hook(sc, 2, "wc_no_scene", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        fade_in(),
    ] + give("figaro_castle_wor") + [
        flag("ScenarioFlag2", 79),
        fade_out(),
        S.op("ChangeMap", i=list(FIGARO_ENGINE)),
        S.op("Exit"),
    ])
    reward_at(sc, 65, "figaro_castle_wor", then="keep")
    return sc


def collapsing_house_map():
    """Tzen in the WoR (WC event/collapsing_house.py, gate Sabin; Map_20330 = PR map 201): the
    collapsing house's start trigger (ev_e_0634, until ScenarioFlag2 53) only with the gate met."""
    sc = S.load("sc_map_20330")
    ms = sc["Mnemonics"]
    assert ms[22]["operands"]["iValues"][:3] == [53, 1, 24] and ms[23]["operands"]["sValues"][0] == "ev_e_0634", "Tzen WoR changed"
    S.hook(sc, 22, "wc_gate", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + gate("collapsing_house", 24) + [            # closed: no start trigger (label 24 -> 25)
        S.op("Jump", i=["@vanilla"]),
    ])
    return sc


def collapsing_house():
    """After the escape (sc_e_0639): the seed's reward instead of Sabin's join, without the
    story dialogue that WC removes from C5/A16-C5/A7C."""
    sc = S.load("sc_e_0639")
    ms = sc["Mnemonics"]
    assert ms[69]["operands"]["sValues"][0].endswith("マッシュ"), "Tzen WoR scene changed"
    assert ms[59]["mnemonic"] == "FadeOut" and ms[73]["mnemonic"] == "FadeIn", "Tzen WoR fades moved"
    for at in (6, 26, 29, 33, 39, 45, 48):
        assert ms[at]["mnemonic"] == "Msg", f"Tzen WoR line {at} changed"
        skip_at(sc, at, f"wc_skip_story_{at}")
    assert ms[1]["mnemonic"] == "SetEntities" and ms[2]["mnemonic"] == "FinishTimer" and ms[55]["label"],         "Tzen WoR's escape changed"
    # and none of the scene with Sabin outside (it played on, silent): the countdown stops, then
    # vanilla's end from its done flags (56: the house's after state, the reward at 69, fade in)
    S.hook(sc, 1, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla")] + S.copy_ops(sc, 2, 3) + [
        S.op("Jump", i=[55])])
    reward_at(sc, 69, "collapsing_house", dark=True)
    return sc


def narshe_wor():
    """Narshe's weapon shop in the WoR (WC event/narshe_wor.py, gate Locke; map 10): the old smith
    gives the seed's two rewards instead of the Ragnarok choice (ScenarioFlag2 161). (In WC he gives
    one, by choice, and a guard in the mines the other.)"""
    sc = S.load("sc_e_0873")
    assert sc["Mnemonics"][1]["mnemonic"] == "BranchByPlayerDirection", "Narshe smith changed"
    S.hook(sc, 1, "wc_narshe_wor", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["narshe_wor"], 1, "@vanilla"),
    ] + gate("narshe_wor", "@closed") + give("narshe_wor") + give("narshe_wor", [("ScenarioFlag2", 161)], n=1, check="narshe_wor_2") + [
        S.op("Exit"),
        S.label("closed"),
        S.op("Exit"),
    ])
    return sc


def doma_wor():
    """Doma Castle in the WoR (WC event/doma_wor.py, gate Cyan; map 300): when Cyan wakes from the
    dream (sc_e_0939_2) the seed's first reward instead of his join. (The dream starts as WC's
    does, with any party once Cyan is recruited: doma_dream. Cyan stays in the party through it.)"""
    sc = S.load("sc_e_0939_2")
    ms = sc["Mnemonics"]
    assert ms[20]["operands"]["sValues"][0].endswith("カイエン") and ms[21]["mnemonic"] == "SysCall", "Doma WoR changed"
    assert [ms[i]["mnemonic"] for i in range(1, 5)] == ["Call", "ScreenEffect", "FadeIn", "ScreenEffect"] and \
        ms[19]["mnemonic"] == "Call", "Cyan's waking changed"
    # WC skips Cyan kneeling, the flashes and his walk (doma_wor.py "cyan walks down and soul
    # cleared", "peak swordsmanship"): the room fades in, the party gathers (19), the reward
    S.hook(sc, 1, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla")] + S.copy_ops(sc, 1, 5) + [
        S.op("Jump", i=[18]),
    ])
    # also skip 21, Cyan learning every Bushido: the game looks Cyan up and throws if he never joined
    reward_at(sc, 20, "doma_wor", then=22)
    return sc


def tower_scenes():
    """The two towers' story scenes (WC event/floating_continent.py statues mods, kefka_tower.py).
    In a WC game:
      * the Floating Continent's statues (sc_e_0594, map 431): Gestahl, Kefka and the Warring
        Triad over three scripts (sc_e_0594 -> sc_e_0594_b in map 641 -> sc_e_0594_1, ~2,400
        instructions of puppets and lines) go straight to their last step (sc_e_0594_2, map 631:
        the collapse's flag, the escape countdown, fc_statues' changes);
      * Kefka's Tower's first landing: no flight round the tower (sc_e_0966_2) and no talk on the
        Falcon's deck about magic vanishing (sc_e_0966_3's first-time branch): straight to the
        three parties (kt_skip's menu), then vanilla's drop from the airship."""
    out = []
    sc = S.load("sc_e_0594")
    ms = sc["Mnemonics"]
    last = S.load("sc_e_0594_1")["Mnemonics"]
    assert ms[171]["operands"]["sValues"][0] == "sc_e_0594_b" and last[109]["operands"]["sValues"][0] == "sc_e_0594_2" and \
        last[109]["operands"]["iValues"][:2] == [631, 4] and last[104]["mnemonic"] == "FadeOut", "the statues changed"
    S.hook(sc, 1, "wc_no_scene", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        copy.deepcopy(last[104]),
        copy.deepcopy(last[109]),
        S.op("Exit"),
    ])
    out.append((sc, S.path("sc_e_0594")))

    sc = S.load("sc_e_0966_1")
    ms = sc["Mnemonics"]
    assert ms[9]["operands"]["iValues"][:3] == [217, 1, 11] and ms[12]["operands"]["sValues"][0] == "sc_e_0966_3", \
        "Kefka's Tower landing changed"
    wc_jump(sc, 9, 11, "wc_no_scene")
    out.append((sc, S.path("sc_e_0966_1")))

    sc = S.load("sc_e_0966_3")
    ms = sc["Mnemonics"]
    assert ms[8]["operands"]["iValues"][:3] == [217, 1, 62] and ms[65]["operands"]["sValues"][0] == "sc_e_0966_3_1", \
        "Kefka's Tower deck changed"
    wc_jump(sc, 8, 61, "wc_no_scene")
    out.append((sc, S.path("sc_e_0966_3")))
    return out


def wor_scenes():
    """World of Ruin story scenes on the way to checks. In a WC game:
      * Figaro's cave (WC event/figaro_castle_wor.py figaro_cave_mod; WC's start sets
        THIEVES_FIGARO_CAVE_TURTLE): at the pond (sc_e_0667 in map 449) Gerad's thieves no longer
        call the turtle and ride off (sc_e_0667_1): the turtle already shuttles (ScenarioFlag2
        76, its route set by the pond's map script); Siegfried no longer runs off at the cave's
        far end (sc_map_30641_7, ev_e_0670: WC hides him);
      * Mobliz (sc_e_0707, map 213): no Katarin's-baby scene before Phunbaba (WC: "katarin
        pregnant scene", "jump straight to attacking phunbaba"): his trigger at once;
      * Gau's father's house in the WoR (sc_e_0959, map 314): with Sabin and Gau, no repairman
        scene (Gau dressed up, the dinner): the old man's lines only."""
    out = []
    f = [p for p in S.all_paths("sc_e_0667") if "Map_30641_2" in p]
    assert len(f) == 1, "Figaro cave's pond not found"
    sc = S.load_path(f[0])
    ms = sc["Mnemonics"]
    assert ms[1]["operands"]["iValues"][:3] == [75, 0, 5] and ms[3]["operands"]["sValues"][0] == "sc_e_0667_1" and \
        ms[6]["operands"]["iValues"][:2] == [448, 1] and not ms[6]["operands"]["sValues"][0], "Figaro cave's pond changed"
    S.hook(sc, 1, "wc_no_scene", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        flag("ScenarioFlag2", 76),
        S.op("Jump", i=[5]),                  # on at 6: into the pond's room
    ])
    out.append((sc, f[0]))

    sc = S.load("sc_map_30641_7")
    ms = sc["Mnemonics"]
    assert ms[3]["operands"]["sValues"][0] == "ev_e_0670" and ms[5]["label"], "Figaro cave's far end changed"
    wc_jump(sc, 2, 5, "wc_no_scene")
    out.append((sc, S.path("sc_map_30641_7")))

    sc = S.load("sc_e_0707")
    ms = sc["Mnemonics"]
    assert ms[49]["operands"]["iValues"][0] == 99 and ms[50]["operands"]["sValues"][0] == "ev_e_0709", "Mobliz WoR's arrival changed"
    S.hook(sc, 1, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla")] + S.copy_ops(sc, 49, 52))
    out.append((sc, S.path("sc_e_0707")))

    sc = S.load("sc_e_0959")
    ms = sc["Mnemonics"]
    assert ms[2]["mnemonic"] == "BranchByCharacter" and ms[2]["operands"]["iValues"][3] == 22 and \
        ms[35]["operands"]["sValues"][0] == "sc_e_0959_1", "Gau's father WoR changed"
    wc_jump(sc, 2, 3, "wc_no_scene")       # as without Sabin: his lines
    out.append((sc, S.path("sc_e_0959")))
    return out


def doma_dream():
    """Cyan's dream (WC event/doma_wor.py): WC skips Elayne and Owain asking the party to save
    Cyan, and their farewell after Wrexsoul (with the sword, the flashes, Cyan kneeling). The
    PR's dream also has three flashbacks of Cyan's family (touch events: training, fishing, the
    "do you love me?" scene). In a WC game:
      * arriving in the dream's Doma (sc_e_0935_1, map 530): its save point and flags, no scene;
      * the flashbacks (sc_map_30850 doesn't place the first two; sc_e_0938's door is a door);
      * after Wrexsoul (sc_e_0939_1): straight to the castle where Cyan wakes (sc_e_0939_2,
        the reward), with vanilla's recovery."""
    out = []
    sc = S.load("sc_e_0935_1")
    ms = sc["Mnemonics"]
    assert ms[6]["mnemonic"] == "FadeIn" and ms[8]["mnemonic"] == "SysCall" and ms[45]["mnemonic"] == "StopBGM" and \
        ms[52]["operands"]["sValues"][0] == "ev_e_0935_1" and ms[56]["operands"]["iValues"][0] == 195 and \
        ms[59]["mnemonic"] == "AutoSave" and ms[60]["mnemonic"] == "ShowMapTitle", "the dream's Doma changed"
    S.hook(sc, 1, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla")]
           + S.copy_ops(sc, 8, 9)          # the dizziness the bridge started: over
           + S.copy_ops(sc, 45, 46)        # vanilla's scene ends with the music stopped
           + S.copy_ops(sc, 52, 53) + S.copy_ops(sc, 56, 59) + S.copy_ops(sc, 6, 7) + S.copy_ops(sc, 59, 62))
    out.append((sc, S.path("sc_e_0935_1")))

    sc = S.load("sc_map_30850")
    ms = sc["Mnemonics"]
    assert ms[4]["operands"]["sValues"][0] == "ev_e_0936" and ms[7]["operands"]["sValues"][0] == "ev_e_0937" and \
        ms[9]["label"], "the family flashbacks changed"
    wc_jump(sc, 3, 9, "wc_no_scene")
    out.append((sc, S.path("sc_map_30850")))

    sc = S.load("sc_e_0938")
    ms = sc["Mnemonics"]
    assert ms[2]["operands"]["iValues"][:3] == [198, 1, 5] and ms[3]["operands"]["sValues"][0] == "sc_e_0938_1" and \
        ms[6]["operands"]["iValues"][:2] == [536, 1], "the third flashback's door changed"
    wc_jump(sc, 2, 5, "wc_no_scene")
    out.append((sc, S.path("sc_e_0938")))

    # Doma's bedroom (sc_map_20451_4, map 300) offers the dream's bed (ev_e_0895) instead of the
    # inn's (ev_e_0894) only with Cyan in the party, exactly 4 members, Mt. Zozo's Cyan back
    # (ScenarioFlag2 109) and the dream not done (199). WC's bed (doma_wor.py sleep_mod) asks for
    # the WoR (this map), the check not done and Cyan recruited (its gate, with -cg): any party.
    # The user's Deck, 2026-11-12 (seed gaadl68ix7j4, Cyan a starting character): "i rested in
    # wor doma and didn't get the doma dream event".
    sc = S.load("sc_map_20451_4")
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "BranchByCharacter" and ms[1]["operands"]["iValues"][3] == 8 and \
        ms[3]["operands"]["iValues"][0] == 109 and ms[4]["operands"]["iValues"][:3] == [199, 1, 8] and \
        ms[6]["operands"]["sValues"][0] == "ev_e_0895" and ms[9]["operands"]["sValues"][0] == "ev_e_0894", \
        "Doma's bedroom changed"
    S.hook(sc, 1, "wc_dream_bed", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag("ScenarioFlag2", 199, 1, 8),                  # the dream done: the inn's bed (on at 9)
        if_flag(WC_FLAGS, CHECK_FLAGS["doma_wor"], 1, 8),
    ] + gate("doma_wor", 8) + [
        S.op("Jump", i=[5]),                                  # the dream's bed (on at 6)
    ])
    out.append((sc, S.path("sc_map_20451_4")))

    # The dream's bed (sc_e_0895). Vanilla puts Cyan's own entity (-3) and slots 2-4 in the beds,
    # moves Cyan to the reserve (MoveReserveCayenne: throws when Cyan never joined) and splits the
    # party (SeparateCorpsAtDreamscape: the first two members left go into two slots, so fewer than
    # 3 besides Cyan leaves nobody); the two are found again in the Dreamscape (ScenarioFlag2 181,
    # 183; until both, ev_e_0900 bars the way to the Stooges). WC's bed keeps the party as it is:
    # the leader in Cyan's bed, no Dream Stooges introducing themselves, and neither vanilla's
    # removal of Cyan (3F 02 00, in the intro WC branches over) nor its "remove first 2 characters
    # in party" (WC: the followers hidden). In a WC game: slots 1-4 in the beds (the leader in
    # Cyan's), the rest, the dream's screen effect, both companions found, into the dream.
    sc = S.load("sc_e_0895")
    ms = sc["Mnemonics"]
    assert ms[11]["operands"]["iValues"][0] == 76 and ms[19]["mnemonic"] == "FadeIn" and \
        ms[54]["mnemonic"] == "ScreenEffect" and ms[56]["mnemonic"] == "FadeOut" and \
        ms[57]["operands"]["sValues"][0] == "控えに移動：カイエン" and \
        ms[58]["operands"]["sValues"][0] == "夢のダンジョン：分離" and \
        ms[59]["operands"]["sValues"][0] == "sc_e_0895_1", "Doma's bed changed"
    assert [ms[i]["operands"]["iValues"][1] for i in range(77, 81)] == [-3, -1002, -1003, -1004], "Doma's beds changed"
    beds = [(84, 88), (90, 94), (96, 100), (102, 106)]       # Cyan's (bottom left), then slots 2-4's
    S.hook(sc, 11, "wc_rest", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Call", i=["@wc_beds"]),
    ] + S.copy_ops(sc, 12, 20) + S.copy_ops(sc, 54, 57) + [
        flag("ScenarioFlag2", 181),
        flag("ScenarioFlag2", 183),
    ] + S.copy_ops(sc, 59, 60) + [
        S.op("Exit"),
    ] + puppets("wc_beds", [-1001, -1002, -1003, -1004], lambda k: S.copy_ops(sc, *beds[k])))
    out.append((sc, S.path("sc_e_0895")))

    sc = S.load("sc_e_0939_1")
    ms = sc["Mnemonics"]
    assert ms[89]["mnemonic"] == "FadeOut" and ms[93]["operands"]["sValues"][0] == "sc_e_0939_2", "after Wrexsoul changed"
    S.hook(sc, 1, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla")] + S.copy_ops(sc, 89, 95))
    out.append((sc, S.path("sc_e_0939_1")))
    return out


def doma_wor_throne():
    """Doma's throne (sc_e_0940, map 304): the seed's second reward instead of Alexander."""
    sc = S.load("sc_e_0940")
    ms = sc["Mnemonics"]
    assert ms[3]["operands"]["sValues"][0] == "E0940_00_304_a_01" and ms[4]["mnemonic"] == "GetItem", "Doma throne changed"
    reward_at(sc, 3, "doma_wor", then=5, n=1, check="doma_wor_throne")
    return sc


def mobliz_wor_start():
    """Mobliz in the WoR (WC event/mobliz_wor.py, gate Terra; map 207): the last Phunbaba scene
    (sc_e_0709_2) only with the gate met."""
    sc = S.load("sc_e_0709_2")
    gate_at(sc, 1, "mobliz_wor")
    return sc


def mobliz_wor():
    """After the last Phunbaba (sc_e_0709_3): the seed's reward before the vanilla done flag
    (ScenarioFlag2 100)."""
    sc = S.load("sc_e_0709_3")
    ms = sc["Mnemonics"]
    assert ms[85]["mnemonic"] == "SetFlag" and ms[85]["operands"]["iValues"][0] == 100, "Mobliz WoR changed"
    assert ms[5]["operands"]["iValues"][0] == 88 and ms[83]["operands"]["iValues"][0] == 636 and \
        ms[84]["mnemonic"] == "FadeIn", "Mobliz's last scene changed"
    # WC (event/mobliz_wor.py "phunbaba 4 battle, esper terra and children scene") skips Terra's
    # scene with the children after the fight: the scene's end positions, then the reward
    S.hook(sc, 5, "wc_no_scene", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Call", i=[636]),
        S.op("Jump", i=[83]),                  # on at 84: fade in, the reward (85)
    ])
    reward_at(sc, 85, "mobliz_wor", then="keep")
    return sc


def mobliz_fenrir():
    """Mobliz's basement (sc_e_0651, map 212): no Fenrir in a WC game (WC starts with GOT_FENRIR:
    the esper is in its reward pool); the vanilla taken flag (ScenarioFlag2 64) as vanilla."""
    sc = S.load("sc_e_0651")
    ms = sc["Mnemonics"]
    assert ms[6]["mnemonic"] == "GetItem" and ms[6]["operands"]["iValues"][0] == 78 and ms[2]["mnemonic"] == "Msg", "Fenrir changed"
    S.hook(sc, 2, "wc_skip", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Jump", i=[6]),                       # on at 7: the taken flag
    ])
    return sc


def doom_gaze_start():
    """Doom Gaze (WC event/doom_gaze.py, gate Setzer): the battle in the WoR sky (sc_e_0964) only
    with the gate met."""
    sc = S.load("sc_e_0964")
    gate_at(sc, 1, "doom_gaze")
    return sc


def doom_gaze():
    """After it, on the Falcon (sc_e_0964_2, map 268): the seed's reward instead of Bahamut."""
    sc = S.load("sc_e_0964_2")
    ms = sc["Mnemonics"]
    assert ms[15]["operands"]["sValues"][0] == "E0965_00_268_a_02" and ms[17]["mnemonic"] == "GetItem", "Doom Gaze changed"
    # the vanilla done flag (ScenarioFlag2 216: no more Doom Gaze in the sky) sits between the
    # line and the magicite: the reward block sets it, then on at 18
    S.hook(sc, 15, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + give("doom_gaze", [("ScenarioFlag2", 216)]) + [S.op("Jump", i=[17])])
    return sc


def magitek_tubes():
    """The Magitek Research Facility (sc_e_0402, map 403; WC event/magitek_factory.py, gate Celes):
    the seed's third factory reward instead of the six espers from the tubes. WC skips the tube
    espers' and Celes's scene (esper_tubes_mod): Kefka, Celes betraying and teleporting him, and
    her leaving the party (SysCall at 132), which in this port ran silently after the reward and
    took a WC game's Celes away. In a WC game the switch gives the reward (gate met, not done),
    marks the scene done (ScenarioFlag1 192, the room's elevator state) and brings the elevator
    up (ev_e_0403), with vanilla's recovery after it; done or gate closed: nothing."""
    sc = S.load("sc_e_0402")
    ms = sc["Mnemonics"]
    assert [ms[i]["mnemonic"] for i in range(124, 130)] == ["GetItem"] * 6 and ms[123]["operands"]["iValues"][0] == 192 and \
        ms[130]["operands"]["sValues"][0] == "ev_e_0403" and ms[132]["operands"]["sValues"][0].endswith("セリス") and \
        [ms[i]["mnemonic"] for i in range(133, 136)] == ["RemoveCondition", "RecoveryHP", "RecoveryMP"], "factory tubes changed"
    S.hook(sc, 1, "wc_tubes", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["magitek_factory_3"], 1, "@done"),
    ] + gate("magitek_factory", "@done") + give("magitek_factory", n=2, check="magitek_factory_3") + [
        flag("ScenarioFlag1", 192),
        S.op("SetEntities", s=["ev_e_0403"]),
    ] + S.copy_ops(sc, 133, 136) + [
        S.label("done"),
        S.op("Exit"),
    ])
    return sc


VECTOR_MAP, VECTOR_POINT = 143, 9       # Vector, where the mine cart comes out (sc_e_0404_7)
VECTOR_WORLD = (WOB_MAP, 27, 121, 189)  # the WoB outside Vector: point 27 at (121, 189)
CRANES = 441                            # the Cranes' party (sc_e_0405_4's EncountBoss)


def magitek_escape():
    """The Magitek Factory's way out (WC event/magitek_factory.py minecart_mod, crane_battle_mod,
    after_cranes_mod): WC skips the elevator ride with Cid, Cid's scene and the escape's story,
    keeps the mine cart ride (its fights and Number 128) and the Cranes, then puts the party
    on the airship outside Vector (WoB 120, 188). In vanilla the cart ends in Vector where Setzer
    joins (sc_e_0405), the Cranes attack the Blackjack, and the story goes on to Zozo (Maduin's
    flashback); the escape flag (ScenarioFlag1 194, set by Cid's scene) also brings that back on
    every later visit. In a WC game:
      * the elevator (sc_e_0403, map 403) takes the party straight down to Cid (map 404; the
        elevator's done flag, ScenarioFlag1 193);
      * talking to Cid (sc_e_0404) starts the mine cart at once, without the escape flag;
      * the cart's arrival in Vector (sc_e_0404_8) is the Cranes fight (no Setzer, no cart
        scene), and after it the party stands outside Vector with the Blackjack under them
        (moved there, as WC does). The fight's follow-up is Setzer's own meet-up script
        (sc_e_0405, in Vector's map package: a battle's follow-up loads from the map it starts
        on, vanilla's sc_e_0405_5 is the deck's), which a WC game reaches no other way now; a
        game that set the escape flag before this change gets the same exit there."""
    out = []
    sc = S.load("sc_e_0403")
    ms = sc["Mnemonics"]
    assert ms[7]["operands"]["iValues"][:2] == [625, 1] and ms[7]["operands"]["sValues"][0] == "sc_e_0403_1", "factory elevator changed"
    S.hook(sc, 1, "wc_elevator", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        flag("ScenarioFlag1", 193),
        S.op("ChangeMap", i=[404, 1]),
        S.op("Exit"),
    ])
    out.append((sc, S.path("sc_e_0403")))

    sc = S.load("sc_e_0404")
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "Jump" and ms[22]["operands"]["iValues"][0] == 194 and \
        ms[23]["operands"]["iValues"][0] == 177 and ms[24]["operands"]["sValues"][0] == "sc_e_0404_1", "Cid's scene changed"
    S.hook(sc, 1, "wc_cid", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        fade_out(),
    ] + S.copy_ops(sc, 23, 25) + [
        S.op("Exit"),
    ])
    out.append((sc, S.path("sc_e_0404")))

    sc = S.load("sc_e_0404_8")
    ms = sc["Mnemonics"]
    assert ms[2]["operands"]["sValues"][0] == "ev_e_0405" and ms[7]["mnemonic"] == "FadeIn", "Vector arrival changed"
    boss = S.load("sc_e_0405_4")["Mnemonics"][236]
    assert boss["mnemonic"] == "EncountBoss" and boss["operands"]["iValues"][0] == CRANES and \
        boss["operands"]["sValues"][0] == "sc_e_0405_5", "the Cranes changed"
    cranes = copy.deepcopy(boss)
    cranes["operands"]["sValues"][0] = "sc_e_0405"
    S.hook(sc, 1, "wc_cranes", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        fade_in(),
        cranes,
        S.op("Exit"),
    ])
    out.append((sc, S.path("sc_e_0404_8")))

    after = S.load("sc_e_0405_5")["Mnemonics"][4]     # vanilla's way out after the Cranes
    assert after["operands"]["iValues"][:2] == list(VECTOR_WORLD[:2]), "after the Cranes changed"
    sc = S.load("sc_e_0405")
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "RemoveTelepoCache" and ms[14]["operands"]["sValues"][0].endswith("セッツァー"), \
        "Setzer's meet-up changed"
    world, point, x, y = VECTOR_WORLD
    S.hook(sc, 1, "wc_after_cranes", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        # as the Serpent Trench's arrival: the Blackjack on the party's tile (layer 1, facing 3)
        S.op("SetVehicle", i=[BLACKJACK, world, x, y, 1, 3]),
        flag("TreasureFlag4", 135),
        S.op("ChangeMap", i=[world, point]),
        S.op("Exit"),
    ])
    out.append((sc, S.path("sc_e_0405")))
    return out


def skip_at(sc, at, label="wc_skip"):
    """In a WC game, the vanilla instruction at `at` doesn't run (on at at + 1)."""
    S.hook(sc, at, label, [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Jump", i=[at]),
    ])


def lone_wolf():
    """The Lone Wolf chase's end on Narshe's cliffs (WC event/lone_wolf.py, gate Mog; map 20):
    Mog's scene (sc_e_0438) gives the seed's first reward instead of Mog (ScenarioFlag1 215)."""
    return talk_reward("sc_e_0438", 1, "lone_wolf", [("ScenarioFlag1", 215)], expect="SysCall")


def lone_wolf_hairpin():
    """The Lone Wolf's loot (sc_e_0439): the seed's second reward instead of the Gold Hairpin."""
    sc = S.load("sc_e_0439")
    ms = sc["Mnemonics"]
    assert ms[3]["operands"]["sValues"][0] == "E0439_00_020_a_03" and ms[10]["mnemonic"] == "GetItem", "hairpin changed"
    gate_at(sc, 1, "lone_wolf", check="lone_wolf_2")
    reward_at(sc, 3, "lone_wolf", n=1, check="lone_wolf_2")
    skip_at(sc, 10)
    return sc


def daryl_tomb():
    """Daryl's tomb's end (WC event/daryl_tomb.py, gate Setzer; map 577): the seed's reward instead
    of Setzer's join.  SNES WC returns to the opened tomb staircase and makes that staircase a
    normal WoR exit; it does not run the vanilla Falcon-acquisition scene."""
    sc = S.load("sc_e_0703")
    assert sc["Mnemonics"][8]["operands"]["sValues"][0].endswith("セッツァー"), "Daryl's tomb changed"
    gate_at(sc, 1, "daryl_tomb")
    S.hook(sc, 8, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        fade_in(),                         # the vanilla join point follows FadeOut
    ] + give("daryl_tomb") + [
        S.op("Exit"),                     # no sc_e_0703_1 Falcon scene
    ])
    return sc


def baren_falls_jump():
    """Baren Falls' ledge (sc_e_0215, map 372; WC event/baren_falls.py, gate Sabin): the jump only
    with the gate met, and no free Cyan / Shadow joining on the way down."""
    sc = S.load("sc_e_0215")
    ms = sc["Mnemonics"]
    assert ms[5]["operands"]["sValues"][0].endswith("カイエン") and ms[7]["operands"]["sValues"][0].endswith("シャドウ"), "falls changed"
    gate_at(sc, 1, "baren_falls")
    skip_at(sc, 5)
    skip_at(sc, 7)
    return sc


def baren_falls():
    """The riverside after the falls (sc_e_0218_2, map 376): the seed's reward instead of naming
    Gau, his join and his running off."""
    sc = S.load("sc_e_0218_2")
    ms = sc["Mnemonics"]
    assert ms[24]["operands"]["sValues"][0].startswith("名前入力") and ms[35]["operands"]["sValues"][0].endswith("ガウ") \
        and ms[39]["operands"]["sValues"][0].startswith("メンバー離脱"), "riverside changed"
    assert ms[10]["operands"]["iValues"][0] == 171 and ms[12]["mnemonic"] == "Call", "riverside's Gau scene changed"
    skip_at(sc, 24)
    # WC ("baren falls gau moving/naming", "gau runs off") has no Gau scene: past the river's
    # flags (9, 10), the reward
    S.hook(sc, 12, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"), S.op("Jump", i=[34])])
    reward_at(sc, 35, "baren_falls", then=40)
    return sc


def south_figaro():
    """South Figaro's basement (WC event/south_figaro.py, gate Celes; map 50): talking to Celes
    gives the seed's reward at once. WC asks nothing and plays no scene (the vanilla question
    "(Remove her bindings.)", Locke and Celes' talk and walk, the heal: all skipped; the user's
    playtest, 2026-11-06): the reward, she's gone (op 41), freed (42), the sleeping guard's
    after-line (45)."""
    sc = S.load("sc_e_0079")
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "Select" and ms[40]["operands"]["sValues"][0].endswith("セリス") \
        and ms[43]["mnemonic"] == "RecoveryHP" and ms[45]["mnemonic"] == "Calc", "South Figaro changed"
    S.hook(sc, 1, "wc_gate", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + gate("south_figaro", "@closed") + [
        S.op("Jump", i=[40 - 1]),
        S.label("closed"),
        S.op("Exit"),
    ])
    reward_at(sc, 40, "south_figaro")
    S.hook(sc, 43, "wc_no_heal", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Jump", i=[45 - 1]),
    ])
    return sc


def figaro_castle_wob():
    """Figaro Castle in the WoB (WC event/figaro_castle_wob.py, gate Edgar; map 34): the seed's
    reward on talking to the throne NPC, with no Edgar story scene.  The reward NPC is entity 18;
    entity 20 is the unrelated soldier below the throne."""
    sc = S.load("sc_e_0029")
    assert sc["Mnemonics"][12]["operands"]["sValues"][0].endswith("エドガー"), "Figaro WoB changed"
    S.hook(sc, 1, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["figaro_castle_wob"], 1, "@closed"),
    ] + gate("figaro_castle_wob", "@closed") + [
        flag("ScenarioFlag1", 17),         # named Edgar: the throne actor stays gone on reload
        S.op("Call", i=["@hide_throne"]),
    ] + give("figaro_castle_wob") + [
        S.op("Exit"),
        S.label("closed"),
        S.op("Exit"),
    ] + puppets("hide_throne", (18,), lambda k: [S.op("Hide", kind=2)]))
    return sc


def figaro_shop(name):
    """WC uses Figaro's post-Kefka WoB inventories from the start.  Those are the copies whose
    products the seed randomizes; the PR's initial copies deliberately remain vanilla."""
    sc = S.load(name)
    ms = sc["Mnemonics"]
    assert all(ms[at]["operands"]["iValues"][:3] == [138, 1, target]
               for at, target in ((4, 7), (32, 35), (61, 64))), f"{name} changed"
    for at, target in ((4, 7), (32, 35), (61, 64)):
        S.hook(sc, at, "wc_shop", [
            if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
            S.op("Jump", i=[target]),
        ])
    return sc


def figaro_shops():
    return [(figaro_shop(name), S.path(name)) for name in ("sc_npc_20051_11_1", "sc_npc_20051_12_1")]


def mt_kolts_start():
    """Mt. Kolts (WC event/mt_kolts.py, gate Sabin; map 343): Vargas only with the gate met."""
    sc = S.load("sc_e_0055")
    gate_at(sc, 1, "mt_kolts")
    return sc


def mt_kolts():
    """After Vargas (sc_e_0055_1): restore the battle data, then finish the check directly.
    WC's esper/item path has no reunion scene; taking the same compact path for every PR reward
    also avoids running a Sabin-specific scene with an arbitrary recruited character."""
    sc = S.load("sc_e_0055_1")
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "SysCall" and ms[44]["operands"]["sValues"][0].endswith("マッシュ"), "Mt. Kolts changed"
    S.hook(sc, 2, "wc_finish", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        flag("ScenarioFlag1", 41),
        fade_in(),
    ] + give("mt_kolts") + [
        S.op("Exit"),
    ])
    return sc


def magitek_024_start():
    """The Magitek Factory's Number 024 (sc_e_0401, map 402): the fight only with the gate met."""
    sc = S.load("sc_e_0401")
    gate_at(sc, 1, "magitek_factory", check="magitek_factory_2")
    return sc


def magitek_024():
    """After Number 024 (sc_e_0401_1): the factory's second reward, before the done flag (ScenarioFlag1 191)."""
    sc = S.load("sc_e_0401_1")
    ms = sc["Mnemonics"]
    assert ms[3]["mnemonic"] == "SetFlag" and ms[3]["operands"]["iValues"][0] == 191, "Number 024 changed"
    reward_at(sc, 3, "magitek_factory", then="keep", n=1, check="magitek_factory_2")
    return sc


def doma_stooges():
    """Cyan's dream (Dreamscape, map 521): after the Three Stooges (sc_e_0901_1) Doma's third reward,
    before the done flag (ScenarioFlag2 184)."""
    sc = S.load("sc_e_0901_1")
    ms = sc["Mnemonics"]
    assert ms[4]["mnemonic"] == "SetFlag" and ms[4]["operands"]["iValues"][0] == 184, "Stooges changed"
    reward_at(sc, 4, "doma_wor", then="keep", n=2, check="doma_wor_3")
    return sc


def doma_stooges_lines():
    """The Dream Stooges' lines (the user's Deck, 2026-11-12: "i'm gettting dialogue on the three
    stooges during doma dream"). The PR gives them no speaker, so the plugin's story rule read
    them as narration. WC event/doma_wor.py stooges_mod removes "back off" (finding each of the
    first two: sc_e_0896, sc_e_0898), "we're the 3 stooges" and "let's rumble" (all three, before
    the fight: sc_e_0901). In a WC game none shows; their hops, flashes and the fight stay."""
    out = []
    for name, ats, keys in (("sc_e_0896", (2,), ["E0896_00_520_a_01"]),
                            ("sc_e_0898", (2,), ["E0896_00_520_a_01"]),
                            ("sc_e_0901", (7, 10), ["E0895_00_300_a_04", "E0901_00_529_a_01"])):
        sc = S.load(name)
        ms = sc["Mnemonics"]
        assert [ms[i]["operands"]["sValues"][0] for i in ats] == keys and all(ms[i]["mnemonic"] == "Msg" for i in ats), \
            f"{name}: the Stooges' lines moved"
        for at in ats:
            skip_at(sc, at, f"wc_no_line_{at}")
        out.append((sc, S.path(name)))
    return out


JOIN, GAU = "メンバー加入：", "ガウ"   # "member joins: ", Gau


def veldt():
    """The Veldt (WC event/veldt.py, gate Gau): Gau's dried-meat scene (sc_e_0239_b, in two maps)
    gives the seed's reward instead of Gau's join (ScenarioFlag1 126 as vanilla)."""
    out = []
    for f in S.all_paths("sc_e_0239_b"):
        sc = S.load_path(f)
        ms = sc["Mnemonics"]
        joins = [n for n, m in enumerate(ms) if m["mnemonic"] == "SysCall"
                 and (m["operands"].get("sValues") or [""])[0] == JOIN + GAU]
        if not joins:
            continue                # Map_40009's copy is a variant without the join
        assert len(joins) == 1, f"{f}: Gau's join at {joins}"
        gate_at(sc, 1, "veldt")
        reward_at(sc, joins[0], "veldt")
        reward = next(s["EntryPoint"] for s in sc["Segments"] if s["Label"] == "wc_reward")
        # The PR follows the dried-meat battle with Gau's full 46-line recruitment scene.
        # WC has no story scene here: mark the vanilla event complete, show the reward, and
        # continue to its ordinary return-map instruction. Keep the vanilla path outside WC.
        S.hook(sc, 2, "wc_skip_story", [
            if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
            flag("ScenarioFlag1", 126),
            fade_in(),
            S.op("Jump", i=[reward]),
        ])
        # The vanilla return launches sc_e_0239 on the world map. That is a second Gau
        # tutorial and it explicitly calls LoseVehicle(Blackjack). Return to the same map
        # point without a continuation script so WC keeps its parked airship.
        S.hook(sc, 107, "wc_return", [
            if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
            S.op("ChangeMap", i=[1, 16]),
        ])
        out.append((sc, f))
    assert out, "no copy of Gau's scene has his join"
    return out


def veldt_world():
    """The world map's Gau script (sc_e_0239, map 1), which the dried meat's first-join branch
    reserves (Current.ReserveScriptGauFirstJoin). Vanilla parks the Blackjack at (1, 1), boards
    and leaves it, and moves the party to the scene map 574 (sc_e_0239_b), which ends at the
    Crescent Mountain cave (world map point 16). WC gives the reward in the battle and the
    party stays where it fought; the plugin turns that reservation away in a WC game
    (WcEvents.GauFirstJoin_Prefix). If it still runs there (a reservation from before that fix,
    a save made mid-way): the reward on the spot if the check is open, never the parking, the
    boarding or the scene map (the user's seed, 2026-11-11: two fades, vanilla Gau, the cave)."""
    sc = S.load("sc_e_0239")
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "Branch" and ms[2]["mnemonic"] == "SetVehicle" and ms[5]["mnemonic"] == "ChangeMap" \
        and ms[5]["operands"]["sValues"][0] == "sc_e_0239_b", "the world map's Gau script changed"
    S.hook(sc, 1, "wc_stay", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["veldt"], 1, "@done"),
        fade_in(),
    ] + give("veldt", flags=[("ScenarioFlag1", 126)]) + [
        S.label("done"),
        S.op("Exit"),
    ])
    return sc


def veldt_battle():
    """The Veldt's battle controller: WC skips the vanilla Leap tutorial and, once dried
    meat succeeds, the three Gau dialogue lines. The BattleJoin call remains because it queues
    the post-battle field script; that script now grants the reward immediately (veldt above)."""
    f = os.path.join(P.VANILLA, "battle_script", "Assets", "GameAssets", "Serial", "Res",
                     "Battle", "BattleScript", "sc_b_0239.json")
    sc = S.load_path(f)
    ms = sc["Mnemonics"]
    assert ms[4]["mnemonic"] == "Branch" and ms[5]["mnemonic"] == "SysCall", "Veldt tutorial changed"
    assert ms[61]["mnemonic"] == "BattleInteractive" and ms[67]["mnemonic"] == "SysCall", "Veldt dried-meat event changed"
    # Jump [5] resumes at 6 (Loop), omitting sc_b_0036's tutorial launch.
    S.hook(sc, 4, "wc_skip_tutorial", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Jump", i=[5]),
    ])
    # Jump [66] resumes at 67: queue the post-battle event, without Gau's dialogue.
    S.hook(sc, 61, "wc_skip_dried_meat_scene", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Jump", i=[66]),
    ])
    # The reward itself is the plugin's, in the dried meat's two SysCalls (event 00 "バトル加入：ガウ",
    # event 01 "ガウ初回予約": WcEvents); its line goes in the battle's own message window right
    # after, before the battle ends: Msg [2, 0], the battle's notice ("Dispelled the curse on the
    # shield!", MSG_SYSTEM_041), as WC shows "Received the Magicite" in the battle (the user,
    # 2026-11-11: not the plugin's overlay box). No fanfare: the game's battle notices have none.
    assert ms[67]["operands"]["sValues"][0] == "バトル加入：ガウ"         and ms[75]["operands"]["sValues"][0] == "ガウ初回予約"         and ms[68]["mnemonic"] == "BattlePause" and ms[76]["mnemonic"] == "BattleForceExitOnSave", "Veldt Gau joins changed"
    for at, name in ((68, "wc_reward_line_00"), (76, "wc_reward_line_01")):
        S.hook(sc, at, name, [
            if_flag(WC_FLAGS, REWARD_LINE, 0, "@vanilla"),
            reset(WC_FLAGS, REWARD_LINE),
            S.op("Msg", i=[2, 0], s=["WC_REWARD"]),
        ])
    return [(sc, f)]


def ultima_intros():
    """The two Ultimas' opening lines (the user, 2026-11-11: "ultima buster shouldn't have in battle
    introductory text"). WC removes them from the SNES AI (data/enemy_script_dialogs.py, always:
    AtmaWeapon's "My name is Atma..." 0x85, Atma's "I'm Atma..." 0x8b); the PR moved them into the
    fights' battle scripts, shown with the battle paused: Ultima Weapon (SNES AtmaWeapon, sc_b_0592,
    4 lines of 3 s) and Ultima Buster (SNES Atma in Kefka's Tower, sc_b_0977, 7 lines of 3 s).
    A WC game jumps over the lines; the pause, its end and Ultima Buster's light stay."""
    out = []
    for name, first, last, after in (("sc_b_0592", 17, 20, "SysCall"), ("sc_b_0977", 19, 25, "PlayBattleEffect")):
        f = os.path.join(P.VANILLA, "battle_script", "Assets", "GameAssets", "Serial", "Res",
                         "Battle", "BattleScript", name + ".json")
        sc = S.load_path(f)
        ms = sc["Mnemonics"]
        assert all(ms[i]["mnemonic"] == "Msg" for i in range(first, last + 1)) and ms[last + 1]["mnemonic"] == after,             f"{name}'s intro changed"
        # Jump [last] resumes at last + 1: the lines skipped
        S.hook(sc, first, "wc_no_intro", [
            if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
            S.op("Jump", i=[last]),
        ])
        out.append((sc, f))
    return out


def ultros_battles():
    """Ultros' fights' battle-script scenes (the user's Deck, 2026-11-12: "i was also getting
    dialogue during an ultros battle"; the boss shuffle had put Ultros 1, with his Lete River
    battle script, in the Dream Stooges' place). WC removes Ultros' battle chatter in all four
    fights (data/enemy_script_dialogs.py cleanup_mod) and the scenes around them; the PR moved
    those scenes into the fights' battle scripts, where the plugin's story rule doesn't reach:
      * sc_b_0065 (Ultros 1, the Lete River): when he dives (Event00) the party's whole raft talk
        plays with battle animations (Sabin's jump into the river: WC's lete_river.py removes the
        scene after Ultros). A WC game: the dive, no "*sploosh!*" / "*glub glub...*", the music
        stopped and the fight's statuses cleared as vanilla, then the fight ends;
      * sc_b_0547 (Ultros 3, Esper Mountain): Event00 is Relm arriving and joining the fight
        (SysCall バトル参戦：リルム) with the sketch scene: WC blanks that battle event
        (esper_mountain.py "esper mountain ultros battle relm appears"): nothing, the event marked
        done; Event_01's defeat lines ("How can this be?...", WC removes 0x50) don't show.
    The PR AI's own chatter for Ultros 2 and 3 (sc_ai_12D/12E) is the plugin's: AI scripts can't
    read story flags (WcEvents.WcRemovedBattleLines)."""
    out = []
    base = os.path.join(P.VANILLA, "battle_script", "Assets", "GameAssets", "Serial", "Res", "Battle", "BattleScript")
    f = os.path.join(base, "sc_b_0065.json")
    sc = S.load_path(f)
    ms = sc["Mnemonics"]
    assert ms[13]["label"] == "Event00" and [ms[i]["operands"]["sValues"][0] for i in (24, 25)] == \
        ["E0065_01_347_b_15", "E0065_01_347_b_16"] and ms[27]["mnemonic"] == "StopBGM" and ms[30]["mnemonic"] == "PlayBGM" \
        and ms[91]["mnemonic"] == "Wait" and ms[92]["mnemonic"] == "BattleForceExit", "Lete River Ultros' battle script changed"
    S.hook(sc, 24, "wc_no_ultros_lines", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"), S.op("Jump", i=[26])])
    S.hook(sc, 30, "wc_no_raft_talk", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"), S.op("Jump", i=[90])])
    out.append((sc, f))

    f = os.path.join(base, "sc_b_0547.json")
    sc = S.load_path(f)
    ms = sc["Mnemonics"]
    assert ms[17]["label"] == "Event00" and ms[27]["operands"]["sValues"][0] == "バトル参戦：リルム" \
        and ms[88]["mnemonic"] == "Calc" and ms[88]["operands"]["iValues"][:2] == [2, 1] and ms[89]["mnemonic"] == "Return" \
        and ms[90]["label"] == "Event_01" and [ms[i]["operands"]["sValues"][0] for i in (94, 95, 96)] == \
        ["E0547_01_425_b_29", "E0547_01_425_b_30", "E0547_01_425_b_31"], "Esper Mountain Ultros' battle script changed"
    S.hook(sc, 18, "wc_no_relm", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"), S.op("Jump", i=[87])])
    S.hook(sc, 94, "wc_no_ultros_lines", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"), S.op("Jump", i=[96])])
    out.append((sc, f))
    return out


def fanatics_tower():
    """The Cultists' Tower's foot (WC event/fanatics_tower.py, gate Strago; map 476): the seed's first
    reward instead of Strago's join (with a full party he'd go to the reserve: skipped too).
    WC removes the Relm/Strago story performance; retain the initial positioning and the vanilla
    cleanup/recovery tail, but go straight to its party-size reward branch."""
    sc = S.load("sc_e_0776")
    ms = sc["Mnemonics"]
    assert ms[59]["operands"]["sValues"][0].endswith("ストラゴス") and ms[64]["operands"]["sValues"][0].endswith("ストラゴス") \
        and ms[65]["operands"]["sValues"][0].startswith("控えに移動"), "Cultists' Tower changed"
    gate_at(sc, 1, "fanatics_tower")
    S.hook(sc, 16, "wc_skip_story", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Jump", i=[54]),              # on at 55: full-party branch, reward, cleanup
    ])
    reward_at(sc, 59, "fanatics_tower")
    reward_at(sc, 64, "fanatics_tower", then=66)
    return sc


def fanatics_tower_dragon():
    """The Holy (White) Dragon at the tower's top (sc_e_0879_1, map 483): no Holy Lance in a WC
    game (WC: it's the dragon's drop instead); the dragon's flag (ScenarioFlag2 166) and the
    countdown (sc_e_0975, where the 8 dragons' rewards are) as vanilla."""
    sc = S.load("sc_e_0879_1")
    ms = sc["Mnemonics"]
    assert ms[3]["operands"]["sValues"][0] == "E0879_00_483_a_01" and ms[5]["mnemonic"] == "GetItem" \
        and ms[4]["operands"]["iValues"][0] == 166, "tower top changed"
    S.hook(sc, 3, "wc_no_lance", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        flag("ScenarioFlag2", 166),
        S.op("Jump", i=[5]),
    ])
    return sc


def magimaster():
    """After the MagiMaster (sc_e_0881_1, map 486): WC's Fanatic's Tower Leader check is done
    (its item is the top chest's, through the chest port)."""
    sc = S.load("sc_e_0881_1")
    ms = sc["Mnemonics"]
    assert ms[3]["mnemonic"] == "SetFlag" and ms[3]["operands"]["iValues"][0] == 168, "MagiMaster changed"
    S.hook(sc, 3, "wc_done", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        flag(WC_FLAGS, CHECK_FLAGS["fanatics_tower_2"]),
    ])
    return sc


def kefka_tower_atma():
    """Kefka's Tower (WC event/kefka_tower.py): after the Ultima Buster (WC's Atma; sc_e_0977_1,
    map 554), once the save point shows, the seed's item."""
    sc = S.load("sc_e_0977_1")
    ms = sc["Mnemonics"]
    assert ms[1]["operands"]["iValues"][0] == 223 and ms[9]["mnemonic"] == "SetEntities" and ms[10]["mnemonic"] == "Exit",         "Ultima Buster changed"
    reward_at(sc, 10, "kefka_tower", then="keep")
    return sc


def wc_jump(sc, at, target, label="wc_jump"):
    """In a WC game, the code at `at` goes to `target` instead (on at target + 1)."""
    S.hook(sc, at, label, [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Jump", i=[target]),
    ])


def fc_arrive():
    """The Floating Continent (WC event/floating_continent.py, gate Shadow; map 431): Shadow's
    scene on arrival (sc_e_0566) gives the seed's first reward instead of Shadow's join."""
    sc = S.load("sc_e_0566")
    assert sc["Mnemonics"][13]["operands"]["sValues"][0].endswith("シャドウ"), "FC arrival changed"
    reward_at(sc, 13, "floating_continent", then=17)
    return sc


def fc_airship_return():
    """(Return to the airship.) on the continent (sc_e_0567): no Shadow leaving (WC: none joined)."""
    sc = S.load("sc_e_0567")
    assert sc["Mnemonics"][9]["mnemonic"] == "BranchByCharacter", "FC return changed"
    wc_jump(sc, 9, 32)
    return sc


def fc_beast():
    """After the Ultima Weapon (sc_e_0592_1): the seed's second reward instead of Shadow leaving."""
    sc = S.load("sc_e_0592_1")
    ms = sc["Mnemonics"]
    assert ms[13]["operands"]["iValues"][0] == 35 and ms[14]["mnemonic"] == "BranchByCharacter" and ms[15]["mnemonic"] == "Exit", \
        "Ultima Weapon changed"
    reward_at(sc, 14, "floating_continent", then=15, n=1, check="floating_continent_2")
    return sc


def no_timer(sc, at):
    """WC's -etn (event timers none): the countdown at `at` (SetTimer) doesn't start. (-etr, random
    timers, is the plugin's: WcEvents.Timer_Prefix sets each countdown from the seed.)"""
    m = sc["Mnemonics"][at]
    assert m["mnemonic"] == "SetTimer" and m["operands"]["iValues"][0] == 0, f"{sc.get('Name')}[{at}] isn't a countdown"
    S.hook(sc, at, "wc_no_timer", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("SysCall", s=["WC:hasflag -etn"]),
        if_flag(WC_FLAGS, SCRATCH, 0, "@vanilla"),
        S.op("Jump", i=[at]),
    ])


def fc_statues():
    """After the statues (sc_e_0594_2): no free Celes join; -etn: no 6:00 escape countdown."""
    sc = S.load("sc_e_0594_2")
    assert sc["Mnemonics"][15]["operands"]["sValues"][0].endswith("セリス"), "FC statues changed"
    wc_jump(sc, 14, 19)
    no_timer(sc, 4)
    return sc


def event_timers():
    """WC's -etn for the opera's 5:00 (sc_e_0330_4, retry sc_e_0343_2) and the collapsing
    house's 6:00 (sc_e_0636)."""
    out = []
    for name, at in (("sc_e_0330_4", 17), ("sc_e_0343_2", 18), ("sc_e_0636", 24)):
        sc = S.load(name)
        no_timer(sc, at)
        out.append((sc, S.path(name)))
    return out


def fc_escape():
    """Back on the Blackjack after the escape (sc_e_0607_1, map 119): the seed's third reward,
    then the deck as it was: no world collapse (WC drops it)."""
    sc = S.load("sc_e_0607_1")
    ms = sc["Mnemonics"]
    assert ms[14]["mnemonic"] == "ChangeMap" and ms[14]["operands"]["sValues"][0] == "sc_e_0607_2", "FC escape changed"
    assert ms[2]["operands"]["sValues"][0] == "背景変更：飛空艇（通常）" and ms[8]["operands"]["sValues"][0] == "背景変更：北の崖FO" \
        and ms[13]["mnemonic"] == "StopEnvSe", "FC escape background changed"
    # the scene has turned the deck's background to the cliff's fade-out (ms[8]): back to the
    # airship's normal one (as ms[2] set it) and the scene's sound stopped (as ms[13]) before the
    # deck reloads through the boarding arrival, as the WC start arrives
    S.hook(sc, 11, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + give("floating_continent", n=2, check="floating_continent_3") + [
        S.op("FadeOut", i=[0, 0, 0, 255], r=[0.7]),
        S.op("StopEnvSe", r=[0.7]),
        S.op("SysCall", s=["背景変更：飛空艇（通常）"]),
        S.op("ChangeMap", i=[119, 1, 0, 0, 1, 0], s=["sc_e_BJinboard_1"]),
        S.op("Exit"),
    ])
    return sc


def veldt_cave_start():
    """The Veldt cave in the WoR (WC event/veldt_cave_wor.py, gate Shadow; sc_e_0744, map 492):
    with the gate closed, just the passage (no Interceptor/Behemoth King scene)."""
    sc = S.load("sc_e_0744")
    ms = sc["Mnemonics"]
    assert ms[2]["mnemonic"] == "Branch" and ms[13]["label"] and ms[14]["mnemonic"] == "ChangeMap", "Veldt cave changed"
    S.hook(sc, 2, "wc_gate", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + gate("veldt_cave_wor", "@closed") + [
        S.op("Jump", i=["@vanilla"]),
        S.label("closed"),
        S.op("Jump", i=[13]),
    ])
    return sc


def veldt_cave(name):
    """After the Behemoth King (both versions, map 491): the seed's reward instead of the flight
    to Thamasa (Shadow's/Relm's bed scenes); both versions marked done (ScenarioFlag2 116/117),
    the party stays in the cave."""
    sc = S.load(name)
    ms = sc["Mnemonics"]
    assert ms[5]["operands"]["sValues"][0] == "E0744_02_492_a_01" and ms[7]["mnemonic"] == "ChangeMap", f"{name} changed"
    S.hook(sc, 5, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + give("veldt_cave_wor", [("ScenarioFlag2", 116), ("ScenarioFlag2", 117)]) + [
        S.op("FadeOut", i=[0, 0, 0, 255], r=[0.7]),
        S.op("ChangeMap", i=[491, 3]),
        S.op("Exit"),
    ])
    return sc


def veldt_cave_shadow():
    return veldt_cave("sc_e_0744_2")


def veldt_cave_relm():
    return veldt_cave("sc_e_0745_2")


def lete_river_start():
    """The Lete River's raft (WC event/lete_river.py, gate Terra; sc_e_0063, map 345)."""
    sc = S.load("sc_e_0063")
    gate_at(sc, 1, "lete_river")
    return sc


def lete_river():
    """After Ultros on the raft (sc_e_0065_1, map 347): the seed's reward, then off the river to
    the world map as a second ride goes (sc_e_0143_1 [31-32]), no scenario select."""
    sc = S.load("sc_e_0065_1")
    ms = sc["Mnemonics"]
    assert ms[16]["mnemonic"] == "FadeOut" and ms[22]["operands"]["sValues"][0] == "sc_e_0065_2", "Lete River changed"
    out = S.copy_ops(S.load("sc_e_0143_1"), 31, 33)
    assert [o["mnemonic"] for o in out] == ["FadeOut", "ChangeMap"], "raft exit changed"
    S.hook(sc, 16, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + give("lete_river") + out + [S.op("Exit")])
    return sc


def sealed_gate_start():
    """The Sealed Gate (WC event/sealed_gate.py, gate Terra; sc_e_0460, map 411): the scene only
    with the gate met, and Terra isn't moved to the reserve (she may not be in the party)."""
    sc = S.load("sc_e_0460")
    ms = sc["Mnemonics"]
    assert ms[5]["operands"]["sValues"][0].endswith("ティナ"), "Sealed Gate changed"
    assert ms[6]["mnemonic"] == "ChangeMap" and ms[6]["operands"]["sValues"][0] == "sc_e_0460_1", "Sealed Gate scene changed"
    gate_at(sc, 1, "sealed_gate")
    skip_at(sc, 5)
    # WC's gate scene is short (event/sealed_gate.py gate_scene_mod: lightning, the reward walks up,
    # back to the cave): no Kefka, no event battle. The PR's sc_e_0460_1 is Kefka's scene and its
    # battle (the party has to fight it out); a WC game goes straight to the gate opening
    # (sc_e_0460_3: the espers burst out, the seed's reward, back to the cave)
    iv = ms[6]["operands"]["iValues"]
    S.hook(sc, 6, "wc_gate_opens", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("ChangeMap", i=iv[:6], s=["sc_e_0460_3"]),
        S.op("Exit"),
    ])
    return sc


def sealed_gate():
    """The gate's end (sc_e_0460_3, map 606): the seed's reward instead of Terra rejoining."""
    sc = S.load("sc_e_0460_3")
    assert sc["Mnemonics"][32]["operands"]["sValues"][0].endswith("ティナ"), "Sealed Gate end changed"
    assert sc["Mnemonics"][29]["mnemonic"] == "FadeOut", "Sealed Gate fade moved"
    assert sc["Mnemonics"][3]["mnemonic"] == "Calc" and sc["Mnemonics"][4]["mnemonic"] == "StopBGM", "Sealed Gate start moved"
    # no espers bursting out (WC's gate_scene_mod is a few seconds): the reward on black, then the cave
    S.hook(sc, 1, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla")] + S.copy_ops(sc, 3, 5) + [
        S.op("Jump", i=[31])])
    reward_at(sc, 32, "sealed_gate", dark=True)
    return sc


def sealed_gate_after():
    """Back in the cave (sc_e_0460_4): not ScenarioFlag1 225 (it starts the Imperial Base's
    espers-attack story, WC's bit 0x79 problem)."""
    sc = S.load("sc_e_0460_4")
    assert sc["Mnemonics"][1]["operands"]["iValues"][0] == 225, "Sealed Gate after changed"
    skip_at(sc, 1)
    return sc


def sealed_gate_map():
    """The gate's map (sc_map_30531_7): once the WC check is done, the after state (the gate
    tile blocked, the shortcut stairs open) as vanilla's 225 gives it."""
    sc = S.load("sc_map_30531_7")
    ms = sc["Mnemonics"]
    assert ms[2]["operands"]["iValues"][:3] == [225, 1, 5], "Sealed Gate map changed"
    S.hook(sc, 2, "wc_done", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["sealed_gate"], 1, 5),
        S.op("Jump", i=["@vanilla"]),
    ])
    return sc


def serpent_trench_start():
    """Crescent Mountain's helmet (WC event/serpent_trench.py, gate Gau; sc_e_0246, map 378).

    WC removes the vanilla party split, Gau/Sabin/Cyan dialogue, reactions and automatic map
    transition.  Finding the helmet only pauses briefly, marks it found, removes its live event
    entities and returns control in the cave.
    """
    sc = S.load("sc_e_0246")
    ms = sc["Mnemonics"]
    assert ms[8]["mnemonic"] == "Msg" and ms[35]["operands"]["iValues"][0] == 133 \
        and ms[37]["mnemonic"] == "ChangeMap", "Crescent Mountain helmet scene changed"
    S.hook(sc, 1, "wc_helmet", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["serpent_trench"], 1, "@closed"),
    ] + gate("serpent_trench", "@closed") + [
        S.op("Wait", r=[0.5]),
        flag("ScenarioFlag1", 133),              # FOUND_DIVING_HELMET
        S.op("Call", i=["@hide_serpent_helmet"]),
        S.op("Exit"),
        S.label("closed"),
        S.op("Exit"),
    ] + puppets("hide_serpent_helmet", (9, 10, 14, 15, 16), lambda k: [S.op("Hide", kind=2)]))
    return sc


def serpent_trench_cave():
    """The cave's map (sc_map_30491_1): WC drops its four other scenes (ev_e_0242-0245)."""
    sc = S.load("sc_map_30491_1")
    ms = sc["Mnemonics"]
    assert ms[20]["label"] == "ev_e_0246" and ms[8]["mnemonic"] == "Branch", "Crescent cave changed"
    wc_jump(sc, 8, 20)
    return sc


def serpent_trench_cliff():
    """Crescent Mountain's exterior (sc_map_30490, map 377): the PR normally creates its
    reusable "Jump in?" trigger (ev_e_0270) only after a vanilla scenario-progress flag. WC
    never sets that story flag; its equivalent condition is having found the diving helmet.
    Keep the player-controlled prompt and route directly into sc_e_0246_2, with no story scene.
    """
    sc = S.load("sc_map_30490")
    ms = sc["Mnemonics"]
    assert ms[2]["operands"]["iValues"][:3] == [138, 0, 6] and ms[5]["operands"]["sValues"][0] == "ev_e_0270", \
        "Crescent Mountain exterior changed"
    S.hook(sc, 2, "wc_serpent_jump", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag("ScenarioFlag1", 133, 0, "@closed"),       # FOUND_DIVING_HELMET
        S.op("SetEntities", s=["ev_e_0270"]),
        S.op("Exit"),
        S.label("closed"),
        S.op("Exit"),
    ])
    return sc


def serpent_trench():
    """Arriving at Nikeah (sc_e_1502_3, map 96): the seed's reward before the autosave (once:
    the dive works again later), the Blackjack moved to Nikeah.  Like PR's native Blackjack
    placement from its interior deck, explicitly use world-object layer 1 and direction 3.
    PR maps layer operand 1 to BottomLayer/native depth 349, matching the world-map player."""
    sc = S.load("sc_e_1502_3")
    ms = sc["Mnemonics"]
    assert ms[8]["mnemonic"] == "AutoSave", "Nikeah arrival changed"
    S.hook(sc, 8, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["serpent_trench"], 1, "@vanilla"),
    ] + gate("serpent_trench", "@vanilla") + give("serpent_trench") + [
        # The Nikeah exit puts the party at 116,61. Airships are boarded from their
        # own tile on the world map, so place the Blackjack under the arriving party.
        S.op("SetVehicle", i=[4, 1, 116, 61, 1, 3]),
        flag("TreasureFlag4", 135),       # keep the Blackjack released / boardable
        S.op("Jump", i=["@vanilla"]),
    ])
    return sc


def nikeah_ferry():
    """Nikeah's docks (sc_map_20121_4): the plain ferry (ev_e_0271), not the one that ends
    Sabin's scenario."""
    sc = S.load("sc_map_20121_4")
    ms = sc["Mnemonics"]
    assert ms[5]["operands"]["iValues"][:3] == [138, 1, 8], "Nikeah docks changed"
    wc_jump(sc, 5, 8)
    return sc


def phantom_train_start():
    """The Phantom Forest's way to the train (WC event/phantom_train.py, gate Sabin; sc_e_0214_0,
    map 353): with the gate closed, out to the world map as after the train."""
    sc = S.load("sc_e_0214_0")
    ms = sc["Mnemonics"]
    assert ms[1]["operands"]["iValues"][:3] == [123, 1, 5] and ms[7]["mnemonic"] == "ChangeMap", "Phantom Forest changed"
    S.hook(sc, 1, "wc_gate", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + gate("phantom_train", "@closed") + [
        S.op("Jump", i=["@vanilla"]),
        S.label("closed"),
        S.op("Jump", i=[5]),
    ])
    return sc


def phantom_train():
    """After the Phantom Train (sc_e_0214_1, map 356): the seed's reward, the train done as the
    station scene leaves it (ScenarioFlag1 123, ScenarioFlag4 168 / not 167), then the forest's
    world-map exit (sc_e_0214_0 [6-7]): no station, no Cyan's family, no Baren Falls jump."""
    sc = S.load("sc_e_0214_1")
    ms = sc["Mnemonics"]
    assert ms[3]["operands"]["sValues"][0] == "E0214_01_356_a_01" and ms[9]["operands"]["sValues"][0] == "sc_e_0214_2", \
        "Phantom Train end changed"
    out = S.copy_ops(S.load("sc_e_0214_0"), 6, 8)
    assert [o["mnemonic"] for o in out] == ["RemoveTelepoCache", "ChangeMap"], "forest exit changed"
    S.hook(sc, 3, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + give("phantom_train", [("ScenarioFlag1", 123), ("ScenarioFlag4", 168)]) + [
        reset("ScenarioFlag4", 167),
        S.op("FadeOut", i=[0, 0, 0, 255], r=[0.7]),
    ] + out + [S.op("Exit")])
    return sc


def south_figaro_cave_map():
    """South Figaro's cave (WC event/south_figaro_cave_wob.py, gate Locke; sc_map_30031_1, map
    329): the Tunnel Armor waits there while the gate is met and the check isn't done (vanilla:
    only between Celes' rescue and the end of Locke's scenario)."""
    sc = S.load("sc_map_30031_1")
    ms = sc["Mnemonics"]
    assert ms[19]["operands"]["sValues"][0] == "ev_e_0086" and ms[20]["label"], "South Figaro cave changed"
    S.hook(sc, 9, "wc_armor", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["south_figaro_cave_wob"], 1, "@vanilla"),
    ] + gate("south_figaro_cave_wob", "@vanilla") + [
        S.op("SetEntities", s=["ev_e_0086"]),
        S.op("Jump", i=[20]),
    ])
    return sc


def south_figaro_cave_start():
    sc = S.load("sc_e_0086")
    gate_at(sc, 1, "south_figaro_cave_wob")
    return sc


def south_figaro_cave():
    """After the Tunnel Armor (sc_e_0086_1): the seed's reward instead of ScenarioFlag1 64 (the
    end of Locke's scenario: it would empty occupied South Figaro, the south_figaro check) and
    the scenario select; the party stays in the cave."""
    sc = S.load("sc_e_0086_1")
    ms = sc["Mnemonics"]
    assert ms[4]["operands"]["iValues"][0] == 64 and ms[10]["mnemonic"] == "Exit", "Tunnel Armor changed"
    reward_at(sc, 4, "south_figaro_cave_wob", then=10)
    return sc


def doma_siege_start():
    """Doma in the WoB (WC event/doma_wob.py, gate Cyan; sc_map_20100, map 75): arriving with
    the gate met and the check not done starts the siege (sc_e_0099_3, where WC starts it),
    entered as vanilla enters it (sc_e_0099_2): out of the screen, then map 75 point 2 with the
    scene as its arrival script. The scene runs this map script itself (ChangeScript [1]) before
    it fades in, so it can't be started from inside it: the ChangeScript tried on 2026-11-10 (to
    hide the entrance's brief flash) left the scene running for good, the screen frozen on its
    last frame (the user's Deck, 2026-11-11). The cut to black is instant, so the entrance
    doesn't show."""
    sc = S.load("sc_map_20100")
    ms = sc["Mnemonics"]
    assert ms[9]["operands"]["iValues"][:3] == [72, 0, 74] and ms[11]["operands"]["sValues"][0] == "ev_e_0100", "Doma changed"
    S.hook(sc, 9, "wc_siege", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["doma_wob"], 1, "@vanilla"),
        if_flag("ScenarioFlag1", 72, 1, "@vanilla"),           # the siege is on
        if_flag("ScenarioFlag2", 41, 1, "@vanilla"),           # the WoR
    ] + gate("doma_wob", "@vanilla") + [
        flag("ScenarioFlag1", 72),                             # (sc_e_0099_3 sets it too; no reload loop)
        S.op("FadeOut", i=[0, 0, 0, 255], r=[0.0]),
        S.op("ChangeMap", i=[75, 2, 0, 0, 1, 0], s=["sc_e_0099_3"]),
        S.op("Exit"),
    ])
    return sc


def doma_siege():
    """After the siege's Captain (sc_e_0110_1, map 75): the seed's reward, Doma back to normal
    (ScenarioFlag1 72 off), its front gate: no Cyan-only healing, forced party, camp return
    (sc_e_0110_2 sets 81: Doma poisoned)."""
    sc = S.load("sc_e_0110_1")
    ms = sc["Mnemonics"]
    assert ms[35]["mnemonic"] == "StopBGM" and ms[47]["operands"]["sValues"][0] == "sc_e_0110_2", "Doma siege end changed"
    assert ms[28]["mnemonic"] == "FadeIn" and ms[29]["mnemonic"] == "Call", "Doma siege's victory changed"
    # no victory scene with Cyan and the soldiers (WC: its lines cut): the field fades in, the reward
    S.hook(sc, 29, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"), S.op("Jump", i=[34])])
    S.hook(sc, 35, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + give("doma_wob") + [
        reset("ScenarioFlag1", 72),
        S.op("FadeOut", i=[0, 0, 0, 255], r=[0.7]),
        S.op("ChangeMap", i=[75, 1]),
        S.op("Exit"),
    ])
    return sc


def phoenix_cave_map():
    """Phoenix Cave's end room (WC event/phoenix_cave.py, gate Locke; sc_map_30781_2, map 499):
    Locke's scene only with the gate met."""
    sc = S.load("sc_map_30781_2")
    ms = sc["Mnemonics"]
    assert ms[39]["operands"]["sValues"][0] == "ev_e_0828" and ms[40]["mnemonic"] == "Jump", "Phoenix Cave changed"
    S.hook(sc, 39, "wc_gate", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + gate("phoenix_cave", "@closed") + [
        S.op("Jump", i=["@vanilla"]),
        S.label("closed"),
        S.op("Jump", i=[39]),
    ])
    return sc


def phoenix_cave():
    """Locke's chest (sc_e_0828): the seed's reward instead of the Kohlingen flashback (Locke,
    the Phoenix magicite and the end room's items there); done as vanilla marks it (ScenarioFlag2
    152, ScenarioFlag4 136), then the Falcon's deck with the two parties merged (sc_e_0831 without
    its Setzer lead)."""
    sc = S.load("sc_e_0828")
    ms = sc["Mnemonics"]
    assert ms[30]["mnemonic"] == "StopBGM" and ms[32]["operands"]["sValues"][0] == "sc_e_0828_1", "Phoenix Cave scene changed"
    assert ms[2]["mnemonic"] == "Call" and ms[9]["mnemonic"] == "Msg", "Locke's reunion changed"
    gate_at(sc, 1, "phoenix_cave")
    # WC (event/phoenix_cave.py) has no reunion: past the gate, straight to the reward
    S.hook(sc, 2, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"), S.op("Jump", i=[29])])
    S.hook(sc, 30, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + give("phoenix_cave", [("ScenarioFlag2", 152), ("ScenarioFlag4", 136)]) + [
        S.op("FadeOut", i=[0, 0, 0, 255], r=[0.7]),
        reset("ScenarioFlag3", 45),
        S.op("ChangePartySlot", i=[1, 1]),
        S.op("ChangeMap", i=[268, 2]),
        S.op("Exit"),
    ])
    return sc


def narshe_battle_map():
    """Narshe's snowfield in the WoB (WC event/narshe_battle.py, no gate; sc_map_20030, map 19):
    Banon waits there in a WC game until the battle is fought (vanilla: only between the
    scenarios' end, ScenarioFlag1 136, and the battle, 137).
    In the WoR the same map holds the snowfield's save point and the Ice Dragon (WC's Narshe Dragon
    check), which vanilla reaches only past the WoB's Narshe flags (ScenarioFlag1 136-138) that a
    WC game never sets: no dragon and no save point there (the user's Deck, 2026-11-12: "i didn't
    see the ice dragon in narshe"). A WC game in the WoR goes straight to that block (229: after
    the Falcon, ScenarioFlag2 97, the save point; the dragon until its flag, ScenarioFlag2 138)."""
    sc = S.load("sc_map_20030")
    ms = sc["Mnemonics"]
    assert ms[24]["operands"]["iValues"][:3] == [136, 0, 235] and ms[26]["operands"]["sValues"][0] == "ev_e_0250", \
        "Narshe snowfield changed"
    assert ms[229]["mnemonic"] == "Nop" and ms[229]["label"] and ms[230]["operands"]["iValues"][:3] == [97, 0, 235] \
        and ms[231]["operands"]["sValues"][0] == "ev_e_0703" and ms[233]["operands"]["iValues"][:3] == [138, 1, 235] \
        and ms[234]["operands"]["sValues"][0] == "ev_e_0779", "Narshe snowfield's WoR block changed"
    S.hook(sc, 24, "wc_banon", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag("ScenarioFlag2", 41, 1, "@wor"),
        if_flag(WC_FLAGS, CHECK_FLAGS["narshe_battle"], 1, "@vanilla"),
        S.op("Jump", i=[24]),                                  # on at 25: Banon's set unless 137
        S.label("wor"),
        S.op("Jump", i=[229]),                                 # on at 230: save point, Ice Dragon
    ])
    return sc


def narshe_lineup(name):
    """The scenario lineup beside Banon (Terra, Locke, ... sc_e_0250-0255) swaps the party's
    leader to that character: nothing in a WC game."""
    sc = S.load(name)
    ms = sc["Mnemonics"]
    assert ms[2]["mnemonic"] == "Branch", f"{name} changed"
    S.hook(sc, 2, "wc_none", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Exit"),
    ])
    return sc


def narshe_lineups():
    return [(narshe_lineup(n), S.path(n)) for n in ("sc_e_0250", "sc_e_0251", "sc_e_0252", "sc_e_0253",
                                                     "sc_e_0254", "sc_e_0255")]


def narshe_battle():
    """After Kefka at Narshe (sc_e_0259_1, map 19): the seed's reward instead of "Where's the
    esper?"; no forced Locke/Cyan/Edgar parties, no Terra leaving; then one party as the story
    picks it (sc_e_0259_5 [57-63]) and Arvis' house, without Terra's esper scene."""
    sc = S.load("sc_e_0259_1")
    ms = sc["Mnemonics"]
    assert ms[12]["operands"]["sValues"][0] == "E0256_04_019_a_02" and ms[22]["operands"]["sValues"][0].endswith("ティナ") \
        and ms[29]["operands"]["sValues"][0] == "sc_e_0259_2", "Narshe battle end changed"
    reward_at(sc, 12, "narshe_battle", then=14)
    wc_jump(sc, 19, 22)
    one_party = S.copy_ops(S.load("sc_e_0259_5"), 57, 65)
    assert [o["mnemonic"] for o in one_party][3] == "SysCall" and one_party[-1]["mnemonic"] == "ChangeMap", "one-party block changed"
    S.hook(sc, 29, "wc_end", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + one_party + [S.op("Exit")])
    return sc


def opera_start(name):
    """The opera's disruption (WC event/opera_house_wob.py, gate Celes): the impresario's "What!?"
    scene (sc_e_0330, or sc_e_0343 after a failure) only with the gate met."""
    sc = S.load(name)
    gate_at(sc, 1, "opera_house_wob")
    return sc


def opera_no_join(name, *at):
    """No free Locke/Celes joins on the way (the rafters, the failure paths)."""
    sc = S.load(name)
    ms = sc["Mnemonics"]
    for i in at:
        assert ms[i]["operands"]["sValues"][0].startswith("メンバー加入"), f"{name}[{i}] changed"
        skip_at(sc, i)
    return sc


def opera_house():
    """After Ultros on the stage (sc_e_0344_2, map 118): the seed's reward while the stage still
    shows, then the opera's after state (ScenarioFlag1 177) and the Blackjack's deck: no curtain,
    no Setzer's cabin, no Celes joining (sc_e_0344_3-5)."""
    sc = S.load("sc_e_0344_2")
    ms = sc["Mnemonics"]
    assert ms[40]["mnemonic"] == "Wait" and ms[43]["operands"]["sValues"][0] == "sc_e_0344_3", "opera end changed"
    assert ms[5]["mnemonic"] == "Call" and ms[7]["mnemonic"] == "FadeIn", "opera's victory scene changed"
    # WC (opera_house_wob.py) cuts Ultros's fall, the impresario and Setzer's entrance down to the
    # reward: the stage fades in, the reward (40), the deck (43)
    S.hook(sc, 5, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla")] + S.copy_ops(sc, 7, 8) + [
        S.op("Jump", i=[39])])
    reward_at(sc, 40, "opera_house_wob", then="keep")
    S.hook(sc, 43, "wc_deck", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        flag("ScenarioFlag1", 177),
        S.op("ChangeMap", i=[119, 1, 0, 0, 1, 0], s=["sc_e_BJinboard_1"]),
        S.op("Exit"),
    ])
    return sc


def opera_scripts():
    return [(opera_start("sc_e_0330"), S.path("sc_e_0330")), (opera_start("sc_e_0343"), S.path("sc_e_0343")),
            (opera_no_join("sc_e_0330_3", 29), S.path("sc_e_0330_3")),
            (opera_no_join("sc_e_0343_1", 29), S.path("sc_e_0343_1")),
            (opera_no_join("sc_e_0341_1", 21, 22), S.path("sc_e_0341_1")),
            (opera_house(), S.path("sc_e_0344_2"))]


CAMP_AT_CYAN = 5          # map-local (0-7; 8 throws): the party went to Cyan's duels this visit
CYAN_DUELS = (8, 25)      # where WC's "Cyan rushes in" puts it (b1348: LoadMap 0x77 at (8, 25))
CAMP_WALK = "ev_e_0114"   # the layer with the trigger at (36, 23), WC's (b1032), vanilla's Leo's talk
CAMP_WALK_NPCS = (26, 27, 28)   # its Leo and two soldiers (WC clears them: imperial_camp.py npc bits)
CAMP_CHEST = "ev_e_0111_1"  # the Star Pendant tent's chest (vanilla: after a scene WC skips)


def puppets(name, entities, body):
    """A routine moving entities as vanilla does (SetPuppet per entity, ExecPuppet): each runs
    body(k) (ops) then Exit. Call "@name"."""
    out = [S.label(name, kind=2)]
    out += [S.op("SetPuppet", i=[k, e, f"@{name}_{k}"], kind=2) for k, e in enumerate(entities)]
    out += [S.op("ExecPuppet", kind=2), S.op("Return", kind=2)]
    for k in range(len(entities)):
        out += [S.label(f"{name}_{k}", kind=2)] + body(k) + [S.op("Exit", kind=2)]
    return out


def imperial_camp_map():
    """The Imperial Camp (WC event/imperial_camp.py, gate Sabin; sc_map_30410, map 350). WC skips
    Leo, both Kefka fights, the poisoning and Doma's fall: in a WC game, with the gate met, the
    camp is in its after-Doma state (ScenarioFlag1 90) where Cyan fights the soldiers; with it
    closed, no scene at all. Never the vanilla first visit (ev_e_0099: into the Doma siege).
    With the check not done, WC's walk: in WC the trigger at (36, 23), down the camp's middle,
    runs Kefka's dash and "Cyan rushes in", which puts the party by Cyan's duels; the PR camp in
    that state had no trigger there, so the party walked all the way across to them (the user's
    playtest, 2026-11-06). The PR's own trigger on that tile (CAMP_WALK, Leo's talk) is set, its
    Leo and soldiers hidden; its script takes the party to the duels (imperial_camp_walk).
    In every WC state its 4th chest (CAMP_CHEST) is there, as WC sets the chest's bit at its
    start; vanilla shows it only after a scene WC skips. With the check done, the camp as it
    stands: its music, chests and exits, no scene (the reward no longer takes the camp off the
    world map: WC's trigger lets the party back in, imperial_camp)."""
    sc = S.load("sc_map_30410")
    ms = sc["Mnemonics"]
    assert ms[11]["operands"]["iValues"][:3] == [81, 1, 15] and ms[16]["operands"]["iValues"][:3] == [90, 1, 57] \
        and ms[55]["label"] and ms[56]["mnemonic"] == "Exit" and ms[30]["operands"]["sValues"][0] == CAMP_CHEST \
        and ms[12]["mnemonic"] == "PlayBGM", "Imperial Camp map changed"
    S.hook(sc, 11, "wc_camp", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("SetEntities", s=[CAMP_CHEST]),
        if_flag(WC_FLAGS, CHECK_FLAGS["imperial_camp"], 1, "@done"),
    ] + gate("imperial_camp", "@closed") + [
        S.op("SetEntities", s=[CAMP_WALK]),
        S.op("Call", i=["@hide_leo"]),
        flag("ScenarioFlag1", 90),
        S.op("Jump", i=[15]),
        S.label("done"),
        S.op("PlayBGM", i=ms[12]["operands"]["iValues"][:2]),   # the camp's own music
        S.op("Exit"),
        S.label("closed"),
        S.op("Jump", i=[55]),
    ] + puppets("hide_leo", CAMP_WALK_NPCS, lambda k: [S.op("Hide", kind=2)]))
    return sc


def imperial_camp_walk():
    """The trigger at (36, 23) (sc_e_0114, vanilla's Leo's talk; set in a WC game by
    imperial_camp_map): the party to Cyan's duels, CYAN_DUELS, where WC's "Cyan rushes in" puts it
    (WC's Kefka dash before it is skipped: no battle in it, Kefka just runs). Once per visit
    (map-local CAMP_AT_CYAN)."""
    sc = S.load("sc_e_0114")
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "SetEntities" and ms[1]["operands"]["sValues"][0] == CAMP_WALK \
        and ms[3]["operands"]["sValues"][0] == "E0114_00_350_a_01", "Leo's talk changed"
    x, y = CYAN_DUELS
    S.hook(sc, 1, "wc_to_cyan", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag("MapLocalVariable", CAMP_AT_CYAN, 1, "@done"),
        S.op("Calc", i=[CAMP_AT_CYAN, 1], s=["MapLocalVariable", "←", "imm"]),
        S.op("FadeOut", i=[0, 0, 0, 255], r=[0.35]),
        S.op("Call", i=["@to_cyan"]),
        S.op("FadeIn", i=[0, 0, 0, 255], r=[0.35]),
        S.label("done"),
        S.op("Exit"),
    # the player and its follower, as vanilla's arrivals place them (sc_e_0133_1), facing Cyan
    ] + puppets("to_cyan", (-1001, 0), lambda k: [S.op("SetPos", i=[x, y], s=["WalkL"] if k == 0 else [], kind=2)]
                + ([S.op("LookAt", i=[3], kind=2)] if k == 0 else [])))
    return sc


def camp_no_join(name, *at):
    """Cyan's fights: no Cyan joining, no condition on him, no move to the reserve."""
    sc = S.load(name)
    ms = sc["Mnemonics"]
    for i in at:
        m = ms[i]
        assert m["mnemonic"] == "AddCondition" or "カイエン" in m["operands"]["sValues"][0], f"{name}[{i}] changed"
        skip_at(sc, i)
    return sc


def imperial_camp():
    """After Cyan's third fight (sc_e_0128_1, map 350): the seed's reward instead of the Magitek
    escape; the escape's steps done (ScenarioFlag1 93-96, ScenarioFlag4 164 off) but not its end
    (97: the camp off the world map; in WC the party may come back, imperial_camp_map), then the
    world map at the camp as sc_e_0133's exit goes there: with its arrival script
    (sc_e_0133_1: the world map's music, the party beside the camp, the fade-in, an autosave).
    The script starts black from the battle (vanilla fades in at ms[10]): the reward fades in
    first, or its line opens on a black screen and waits for a press nobody sees (the user's
    playtest, 2026-11-06: "the screen just went black")."""
    sc = S.load("sc_e_0128_1")
    ms = sc["Mnemonics"]
    assert ms[3]["mnemonic"] == "SetVehicle" and ms[83]["operands"]["iValues"][0] == 93, "Imperial Camp end changed"
    assert ms[10]["mnemonic"] == "FadeIn", "Imperial Camp fade-in moved"
    out = S.load("sc_e_0133")["Mnemonics"]
    exit_ = next(m for m in out if m["mnemonic"] == "ChangeMap")
    assert exit_["operands"]["iValues"][:2] == [1, 11] and exit_["operands"]["sValues"][0] == "sc_e_0133_1", "camp exit changed"
    S.hook(sc, 3, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("FadeIn", i=[0, 0, 0, 255], r=[0.7]),
    ] + give("imperial_camp", [("ScenarioFlag1", f) for f in (93, 94, 95, 96)]) + [
        reset("ScenarioFlag4", 164),
        S.op("FadeOut", i=[0, 0, 0, 255], r=[0.7]),
        S.op("ChangeMap", i=exit_["operands"]["iValues"][:6], s=["sc_e_0133_1"]),
        S.op("Exit"),
    ])
    return sc


def camp_scripts():
    return [(imperial_camp_map(), S.path("sc_map_30410")),
            (imperial_camp_walk(), S.path("sc_e_0114")),
            (camp_no_join("sc_e_0125", 15, 16), S.path("sc_e_0125")),
            (camp_no_join("sc_e_0125_1", 13), S.path("sc_e_0125_1")),
            (camp_no_join("sc_e_0127", 15), S.path("sc_e_0127")),
            (camp_no_join("sc_e_0127_1", 13), S.path("sc_e_0127_1")),
            (camp_no_join("sc_e_0128", 15), S.path("sc_e_0128")),
            (imperial_camp(), S.path("sc_e_0128_1"))]


def moogle_defense_map():
    """Narshe's mines where the moogles fight (WC event/narshe_moogle_defense.py, gate Mog;
    sc_map_30011_2, map 318). WC asks at Arvis'; the PR has no free trigger there, so entering
    these mines in the WoB with the gate met (check not done, defense not running) starts the
    defense's opening scene (sc_e_0017_6), which asks first. ScenarioFlag4 130 keeps the map's
    reload from asking again."""
    sc = S.load("sc_map_30011_2")
    ms = sc["Mnemonics"]
    assert ms[22]["operands"]["iValues"][:3] == [13, 0, 26] and ms[24]["operands"]["sValues"][0] == "ev_e_0018", "mines changed"
    S.hook(sc, 22, "wc_moogles", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["narshe_moogle_defense"], 1, "@vanilla"),
        if_flag(WC_FLAGS, MOOGLE_OFFERED, 1, "@vanilla"),
        if_flag("ScenarioFlag2", 41, 1, "@vanilla"),
        if_flag("ScenarioFlag1", 13, 1, "@vanilla"),
    ] + gate("narshe_moogle_defense", "@vanilla") + [
        flag(WC_FLAGS, MOOGLE_OFFERED),
        S.op("ChangeMap", i=[318, 1, 0, 0, 1], s=["sc_e_0017_6"]),
        S.op("Exit"),
    ])
    return sc


def moogle_defense_ask():
    """The defense's opening (sc_e_0017_6): in a WC game it asks first; (Not now.) leaves the
    party in the mines (asked again next time they're entered)."""
    sc = S.load("sc_e_0017_6")
    ms = sc["Mnemonics"]
    assert ms[1]["operands"]["sValues"][0] == "ev_e_0017" and ms[3]["mnemonic"] == "FadeIn", "moogle opening changed"
    S.hook(sc, 1, "wc_ask", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Msg", s=["WC_MOOGLE_ASK"]),
        S.op("Select", s=["WC_MOOGLE_YES", "WC_MOOGLE_NO"]),
        selection(0, "@vanilla"),
        reset(WC_FLAGS, MOOGLE_OFFERED),
        S.op("FadeIn", i=[0, 0, 0, 255], r=[0.7]),
        S.op("Exit"),
    ])
    return sc


def moogle_defense_party():
    """The defense's parties (sc_e_0017_7): no Locke joining; party 1 keeps its leader with three
    moogles (vanilla: Locke and three moogles); Mog's and the third party as vanilla."""
    sc = S.load("sc_e_0017_7")
    ms = sc["Mnemonics"]
    assert ms[12]["operands"]["sValues"][0].endswith("ロック") and ms[14]["operands"]["iValues"][:5] == [1, 2, 19, 20, 21], \
        "moogle parties changed"
    skip_at(sc, 12)
    S.hook(sc, 14, "wc_party", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("OrganizePartySlot", i=[1, -2, 19, 20, 21]),
        S.op("Jump", i=[14]),
    ])
    return sc


def moogle_defense():
    """After the Marshal (sc_e_0018_2, map 318): the moogles leave, then the seed's reward; no
    Locke/Terra party and escape story (ScenarioFlag1 14 = the defense over, as sc_e_0018_5 sets
    it); multi-party off and one party picked (as after Narshe's battle), back in the mines."""
    sc = S.load("sc_e_0018_2")
    ms = sc["Mnemonics"]
    assert ms[13]["operands"]["sValues"][0].startswith("メンバー離脱") and ms[14]["operands"]["iValues"][:3] == [1, 2, 1], \
        "Marshal end changed"
    one_party = S.copy_ops(S.load("sc_e_0259_5"), 57, 64)
    assert ms[10]["mnemonic"] == "FadeOut", "Marshal end fade moved"
    S.hook(sc, 14, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        fade_in(),                                    # faded out at ms[10]: the reward's line shows
    ] + give("narshe_moogle_defense", [("ScenarioFlag1", 14)]) + [
        reset("ScenarioFlag3", 41),
        reset("ScenarioFlag3", 45),
        reset(WC_FLAGS, MOOGLE_OFFERED),
        S.op("ChangePartySlot", i=[1, 1]),
    ] + one_party + [
        S.op("ChangeMap", i=[318, 1, 0, 0, 1]),
        S.op("Exit"),
    ])
    return sc


def blackjack_deck():
    """The Blackjack's deck (sc_map_20170, map 119): the wheel (ev_e_0421) is only placed after
    the Magitek Factory's escape (ScenarioFlag1 194, which WC games never set: it would also bring
    that escape back in Vector): always in a WC game."""
    sc = S.load("sc_map_20170")
    ms = sc["Mnemonics"]
    assert ms[28]["operands"]["iValues"][:3] == [194, 0, 30] and ms[29]["operands"]["sValues"][0] == "ev_e_0421", "deck changed"
    wc_jump(sc, 28, 28)
    # The Floating Continent trip (the user's Deck, 2026-11-12: "flying to the floating continent
    # just puts me back on the blackjack deck and i'm able to take the wheel again"): the deck's
    # air force state (帝国空軍戦中: no wheel, the attack's entities, the fights' counter) sits
    # under the story's airship states (178 / 194 / 226 / ScenarioFlag2 26), which a WC game
    # never sets, so the trip's deck (sc_e_0560_2 sets ScenarioFlag2 29 and runs this script)
    # was the plain deck with its wheel. In a WC game: straight to the trip's own test (29 set,
    # 30 = landed not yet), else the plain deck as before.
    assert ms[2]["operands"]["iValues"][:3] == [178, 0, 24] and ms[17]["label"] == "魔大陸突入中" \
        and ms[18]["operands"]["iValues"][:3] == [29, 0, 24] and ms[19]["operands"]["iValues"][:3] == [30, 1, 24] \
        and ms[33]["label"] == "帝国空軍戦中", "deck's air force state changed"
    wc_jump(sc, 2, 17)
    return sc


def fc_airship_trip():
    """The flight to the Floating Continent (WC event/floating_continent.py airship_battle_mod),
    after the wheel. In a WC game:
      * the air force's alert before the first fight (sc_e_1056: "Uh-oh! It's the Imperial air
        force!", dialog $084F, CA/592F) doesn't show;
      * 3 of the 6 fights (WC: CA/5978 branches to CA/59BB): after the first, on to the fifth,
        with what vanilla does after the fourth (the counter, the thing coming closer) but its
        "There's something strange up ahead!" (WC: a pause instead, CA/59BB);
      * the landing (sc_e_0565_3) has no "Just ahead are Kefka, Gestahl..." (CA/5A68);
      * boarding from outside (sc_e_BJinboard_1) with the trip begun and not landed: a trip given
        up (the wheel was open on the trip's deck before the deck fix, and saves could keep its
        flag): the flag goes, so the deck is the plain one and the wheel offers the trip again.
        (A trip under way never boards: its deck comes from the wheel, the flight and battles.)"""
    out = []
    sc = S.load("sc_e_1056")
    ms = sc["Mnemonics"]
    assert [ms[i]["operands"]["sValues"][0] for i in (2, 3)] == ["E0560_01_119_a_01", "E0560_01_119_a_02"] \
        and ms[5]["mnemonic"] == "Encount", "air force alert changed"
    wc_jump(sc, 2, 3)
    out.append((sc, S.path("sc_e_1056")))

    sc = S.load("sc_e_1056_1")
    ms = sc["Mnemonics"]
    assert ms[3]["mnemonic"] == "Calc" and ms[3]["operands"]["iValues"][:2] == [1, 1] and \
        ms[5]["operands"]["sValues"][0] == "sc_e_1058", "first air force fight's end changed"
    after4 = S.load("sc_e_1058_1")["Mnemonics"]
    assert after4[18]["label"] == "4回目後" and after4[19]["operands"]["iValues"][:2] == [1, 4] \
        and after4[24]["operands"]["sValues"][0] == "E0560_02_119_a_01" and after4[25]["mnemonic"] == "AssignMoveRoute", \
        "air force's fourth fight changed"
    S.hook(sc, 3, "wc_three_fights", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Calc", i=[1, 4], s=["MapLocalVariable", "←", "imm"]),
        S.op("SetTimer", i=[1], s=["sc_e_1058"], r=[8]),
        S.op("FadeIn", i=[0, 0, 0, 255], r=[0.7]),
        S.op("Wait", r=[0.7]),
        S.op("AssignMoveRoute", i=[4, 201708]),
        S.op("Exit"),
    ])
    out.append((sc, S.path("sc_e_1056_1")))

    sc = S.load("sc_e_0565_3")
    ms = sc["Mnemonics"]
    assert ms[9]["operands"]["sValues"][0] == "E0559_00_119_a_01" and ms[3]["operands"]["iValues"][0] == 30, \
        "Floating Continent landing changed"
    skip_at(sc, 9, "wc_no_line")
    out.append((sc, S.path("sc_e_0565_3")))

    sc = S.load("sc_e_BJinboard_1")
    ms = sc["Mnemonics"]
    assert ms[3]["mnemonic"] == "ChangeScript" and ms[3]["operands"]["sValues"][0] == "sc_map_20170", "Blackjack boarding changed"
    S.hook(sc, 3, "wc_trip_given_up", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag("ScenarioFlag2", 29, 0, "@vanilla"),
        if_flag("ScenarioFlag2", 30, 1, "@vanilla"),
        reset("ScenarioFlag2", 29),
    ])
    out.append((sc, S.path("sc_e_BJinboard_1")))
    return out


def narshe_town():
    """Narshe (sc_Map_20010, map 3): the PR's town script is the story in order (the raid, the
    bridge chase, the gate guards...); in a WC game none of it: on to its later entity sets (the
    WoB: from ev_e_0089, the secret entrance; the WoR: ev_e_0703)."""
    sc = S.load("sc_Map_20010")
    ms = sc["Mnemonics"]
    assert ms[23]["operands"]["iValues"][:3] == [11, 1, 38] and ms[66]["label"] == "ev_e_0089" and ms[104]["label"] == "ev_e_0703", \
        "Narshe town changed"
    S.hook(sc, 23, "wc_narshe", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag("ScenarioFlag2", 41, 1, "@wor"),
        S.op("Jump", i=[65]),
        S.label("wor"),
        S.op("Jump", i=[103]),
    ])
    # the WoR's thief who walks up at the town gate (ev_e_0777: "Haven't we met someplace
    # before?"); WC's start sets MET_LONE_WOLF_WOR (event/narshe_wor.py), so it never plays
    assert ms[149]["operands"]["iValues"][:3] == [22, 1, 151] and ms[150]["operands"]["sValues"][0] == "ev_e_0777", \
        "Narshe's WoR thief changed"
    skip_at(sc, 150, "wc_no_scene")
    return sc


# Story scenes outside WC's checks (the user's playtest, 2026-11-12: Setzer and Cid's engine-room
# scene on the Blackjack). The plugin already skips their lines (WcEvents.SkipStoryLine), which
# left them playing as silent animations. In a WC game each is cut where it starts: its map
# script doesn't place it (`no_layer`) or its trigger script goes straight to what the scene
# leads to. tools/wc_scene_audit.py lists the scenes a WC game can still reach.
NO_SCENE_LAYERS = [
    # (map script, index of the SetEntities, layer, what it is)
    ("sc_Map_20101_1", 10, "ev_e_0119", "Doma Castle: Cyan runs to the poisoned king (WC sets CYAN_FOUND_POISONED_KING_DOMA)"),
    ("sc_Map_20101_2", 10, "ev_e_0156", "Doma Castle: the poisoned soldiers' room (WC: CYAN_FOUND_POISONED_FAMILY_DOMA)"),
    ("sc_map_30591_1", 3, "ev_e_0551", "Esper Mountain's end: the espers and Yura (WC sets FOUND_ESPERS_ESPER_MOUNTAIN)"),
    ("sc_map_30521_2", 12, "ev_e_0393", "Magitek Factory: Kefka throws Ifrit and Shiva away"),
    ("sc_map_20081_2", 19, "ev_e_1108", "the Returners' hideout: a wounded soldier talks in his sleep"),
    ("sc_map_20371_3", 5, "ev_e_0671", "Figaro Castle WoR's prison: Gerad overheard (WC hides him: GERAD_PRISON_FIGARO_CASTLE)"),
    ("sc_map_30470", 9, "ev_e_0216", "Baren Falls: the talk on the cliff (Shadow leaves)"),
    ("sc_map_20381_1", 2, "ev_e_0681", "Kohlingen WoR's inn: Setzer, who joins when talked to (WC never sets SETZER_KOHLINGEN_INN)"),
    ("sc_map_30430", 3, "ev_e_0160", "the Phantom Train's platform: finding the train (WC: phantom train skip find train event)"),
]

# the Phantom Train's friendly ghosts, who join when talked to (WC: "do not allow recruiting
# ghosts", recruit_ghosts_mod)
TRAIN_GHOSTS = ["sc_e_0165", "sc_e_0170", "sc_e_0175", "sc_e_0178", "sc_e_0192", "sc_e_0200"]
# where the roof's ghosts stand once they've surrounded the party (sc_map_30440_1 "ghost_surround",
# the map's own state after the scene): (entity, x, y, animation)
TRAIN_ROOF_GHOSTS = [(6, 119, 5, "Default"), (7, 122, 6, "WalkR"), (8, 128, 5, "Default"), (9, 126, 6, "WalkL"),
                     (10, 118, 5, "Default"), (11, 121, 6, "WalkR"), (12, 129, 5, "Default"), (13, 127, 6, "WalkL"),
                     (14, 120, 6, "WalkR"), (15, 119, 6, "Default"), (80, 128, 6, "Default")]


def phantom_train_scenes():
    """The Phantom Train (WC event/phantom_train.py: find_train_mod, enter_train_mod,
    recruit_ghosts_mod, engineer_switch_mod, escape_ghosts_mod, restaurant_mod, ziegfried_mod).
    WC keeps the dungeon (the roof fight, Sabin's jump to the front cars, the lever, the dining
    car's meal, Siegfried, the engine) and cuts its story. In a WC game:
      * boarding (sc_e_0161, map 355): straight into the first car, the train departs with no
        scene (sc_e_0161_1), and the "won't open" scene's flag (ScenarioFlag1 109, with its
        layer: the car's ghosts) is set, so the front door leads out at once;
      * Sabin's lever in the third car (sc_e_0172): nothing;
      * the friendly ghosts (TRAIN_GHOSTS): nothing (no joining);
      * after the roof fight (sc_e_0185_2, map 624): the ghosts already stand round the party
        (where the map puts them after vanilla's scene), the party where it ends;
      * the ladder (sc_e_0186): its flag and the jump's trigger, no ghosts closing in;
      * the plan to detach the cars (sc_e_0188): its flag, then on into the sixth car;
      * the lever there (sc_e_0189): the cars detached at once (114; vanilla goes outside to
        watch them roll away), with vanilla's second pull's shake (sc_e_0190, 115);
      * the dining car (sc_e_0196, sc_e_0197): the meal's recovery and its line only;
      * Siegfried (sc_e_0202): the fight."""
    out = []

    def done(sc, name):
        out.append((sc, S.path(name)))

    sc = S.load("sc_e_0161")
    ms = sc["Mnemonics"]
    assert ms[32]["mnemonic"] == "FadeOut" and ms[33]["operands"]["sValues"][0] == "sc_e_0161_1", "the train's door changed"
    S.hook(sc, 1, "wc_board", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla")] + S.copy_ops(sc, 32, 34) + [S.op("Exit")])
    done(sc, "sc_e_0161")

    sc = S.load("sc_e_0161_1")
    ms = sc["Mnemonics"]
    assert ms[1]["operands"]["sValues"][0] == "ev_e_0161" and ms[3]["mnemonic"] == "FadeIn" and \
        ms[16]["mnemonic"] == "PlayEnvSe" and ms[19]["mnemonic"] == "PlayBGM" and \
        [ms[i]["operands"]["iValues"][0] for i in (42, 43, 44)] == [108, 167, 166] and ms[45]["mnemonic"] == "ShowMapTitle", \
        "the train's departure changed"
    S.hook(sc, 1, "wc_departed", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("SetEntities", s=["ev_e_0163"]),       # as sc_e_0163 leaves the car (its ghosts)
    ] + S.copy_ops(sc, 16, 17) + S.copy_ops(sc, 19, 20) + S.copy_ops(sc, 42, 45) + [
        flag("ScenarioFlag1", 109),                  # the rear door's scene: seen
    ] + S.copy_ops(sc, 3, 4) + S.copy_ops(sc, 45, 46) + [S.op("Exit")])
    done(sc, "sc_e_0161_1")

    for name in ["sc_e_0172"] + TRAIN_GHOSTS:
        sc = S.load(name)
        ms = sc["Mnemonics"]
        if name == "sc_e_0172":
            assert ms[27]["operands"]["iValues"][0] == 6 and ms[3]["mnemonic"] == "Msg", "Sabin's lever changed"
        else:
            assert any(m["mnemonic"] == "SysCall" and m["operands"]["sValues"][0].startswith("メンバー加入")
                       for m in ms) and ms[1]["mnemonic"] == "Msg", f"{name}: a ghost's join changed"
        S.hook(sc, 1, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"), S.op("Exit")])
        done(sc, name)

    sc = S.load("sc_e_0185_2")
    ms = sc["Mnemonics"]
    assert ms[4]["operands"]["iValues"][0] == 159 and ms[156]["operands"]["iValues"][0] == 110 and \
        ms[157]["mnemonic"] == "AutoSave" and ms[898]["label"] == "event_end", "the roof's surround changed"
    ghosts = TRAIN_ROOF_GHOSTS
    S.hook(sc, 4, "wc_surrounded", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Call", i=["@wc_ghosts"]),
        S.op("Call", i=[898]),                      # vanilla's end: the party at (124, 6), the actors hidden
        fade_in(),
        flag("ScenarioFlag1", 110),
        S.op("AutoSave"),
        S.op("Exit"),
    ] + puppets("wc_ghosts", [g[0] for g in ghosts],
                lambda k: [S.op("SetPos", i=[ghosts[k][1], ghosts[k][2]], s=[ghosts[k][3]], kind=2)]))
    done(sc, "sc_e_0185_2")

    sc = S.load("sc_e_0186")
    ms = sc["Mnemonics"]
    assert ms[10]["operands"]["iValues"][0] == 111 and ms[11]["operands"]["sValues"][0] == "ev_e_0187", "the roof's ladder changed"
    S.hook(sc, 1, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla")] + S.copy_ops(sc, 10, 12) + [S.op("Exit")])
    done(sc, "sc_e_0186")

    sc = S.load("sc_e_0188")
    ms = sc["Mnemonics"]
    assert ms[1]["operands"]["iValues"][:3] == [113, 1, 81] and ms[81]["label"] and \
        ms[86]["operands"]["iValues"][:2] == [362, 1], "the plan to detach the cars changed"
    S.hook(sc, 2, "wc_no_scene", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        flag("ScenarioFlag1", 113),
        S.op("Jump", i=[81]),                       # vanilla's second visit: into the sixth car
    ])
    done(sc, "sc_e_0188")

    sc = S.load("sc_e_0189")
    ms = sc["Mnemonics"]
    assert ms[1]["operands"]["iValues"][:3] == [114, 1, 9] and ms[10]["operands"]["sValues"][0] == "sc_e_0190", \
        "the detaching lever changed"
    S.hook(sc, 2, "wc_detach", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        flag("ScenarioFlag1", 114),                 # detached (sc_e_0189_1's flag)
        S.op("Jump", i=[9]),                        # the second pull's shake (sc_e_0190: 115)
    ])
    done(sc, "sc_e_0189")

    for name, at, heal in (("sc_e_0196", 2, 22), ("sc_e_0197", 9, 51)):
        sc = S.load(name)
        ms = sc["Mnemonics"]
        assert [ms[i]["mnemonic"] for i in range(heal, heal + 6)] == \
            ["PlaySE", "Flash", "RecoveryHP", "RecoveryMP", "RemoveCondition", "Msg"], f"{name}: the meal changed"
        S.hook(sc, at, "wc_meal", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla")] + S.copy_ops(sc, heal, heal + 6) + (
            [flag("ScenarioFlag1", 116)] if name == "sc_e_0196" else []) + [S.op("Exit")])
        done(sc, name)

    sc = S.load("sc_e_0202")
    ms = sc["Mnemonics"]
    assert ms[15]["mnemonic"] == "EncountBoss" and ms[15]["operands"]["sValues"][0] == "sc_e_0202_1", "Siegfried changed"
    S.hook(sc, 1, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla")] + S.copy_ops(sc, 15, 17))
    done(sc, "sc_e_0202")
    return out


def no_layer(name, at, layer, why):
    """In a WC game, a map script doesn't place a story scene's entity layer (its trigger and
    actors): the SetEntities at `at` is skipped."""
    sc = S.load(name)
    ms = sc["Mnemonics"]
    assert ms[at]["mnemonic"] == "SetEntities" and ms[at]["operands"]["sValues"][0] == layer, f"{name}: {why}: changed"
    skip_at(sc, at, "wc_no_scene")
    return sc


DARILL_EXIT = (WOR_MAP, 11)   # the World of Ruin at Darill's tomb (Map_30650's way out)


def story_scenes():
    """Every cut story scene: [(script, vanilla file)]."""
    out = [(no_layer(*row), S.path(row[0])) for row in NO_SCENE_LAYERS]

    # The Blackjack's stairs down to the engine room (sc_e_1033, map 121): after Gestahl's dinner
    # (ScenarioFlag2 1, which WC's start sets) and until the scene's own flag (ScenarioFlag1 73)
    # they play Setzer and Cid's talk about Darill (sc_e_1033_1, map 122). In a WC game: the
    # stairs, nothing else (vanilla's own branch when no scene is due).
    sc = S.load("sc_e_1033")
    ms = sc["Mnemonics"]
    assert ms[4]["operands"]["sValues"][0] == "sc_e_1033_1" and ms[5]["label"] and \
        ms[6]["mnemonic"] == "ChangeMap" and ms[6]["operands"]["iValues"][:2] == [122, 1] and not ms[6]["operands"]["sValues"][0], \
        "the Blackjack's engine room stairs changed"
    wc_jump(sc, 1, 5, "wc_no_scene")
    out.append((sc, S.path("sc_e_1033")))

    # Sabin's cabin (the door, sc_e_0046_0, map 36): until Sabin's scenario reaches the cabin's
    # old man (ScenarioFlag1 36) or Vargas (41), going in plays "What's that smell?" and the
    # tea scene (sc_e_0046, map 37). In a WC game: the door.
    sc = S.load("sc_e_0046_0")
    ms = sc["Mnemonics"]
    assert ms[4]["operands"]["sValues"][0] == "sc_e_0046" and ms[5]["label"] and \
        ms[6]["operands"]["iValues"][:2] == [37, 1] and not ms[6]["operands"]["sValues"][0], "Sabin's cabin door changed"
    wc_jump(sc, 2, 5, "wc_no_scene")
    out.append((sc, S.path("sc_e_0046_0")))

    # South Figaro's pub (sc_map_20071_4, map 42): Shadow sits there from his scene (ScenarioFlag1
    # 27, which WC's start sets) until Vargas (41); talking to him plays his introduction and
    # his naming screen. WC hides him (event/south_figaro.py: SHADOW_SOUTH_FIGARO_PUB). In a WC
    # game: the pub without him (vanilla's branch for that, with the pub's music).
    sc = S.load("sc_map_20071_4")
    ms = sc["Mnemonics"]
    assert ms[8]["operands"]["iValues"][:3] == [27, 0, 13] and ms[11]["operands"]["sValues"][0] == "ev_e_0041" and \
        ms[13]["label"], "South Figaro's pub changed"
    wc_jump(sc, 8, 13, "wc_no_scene")
    out.append((sc, S.path("sc_map_20071_4")))

    # Kohlingen (Rachel's house, sc_e_0283, map 102): talking to the herbalist plays Locke's
    # flashback with Rachel. WC deletes that event (event/kohlingen.py rachel_house_mod). In a
    # WC game: his line from after the flashback (sc_e_0288).
    sc = S.load("sc_e_0283")
    ms = sc["Mnemonics"]
    assert ms[1]["operands"]["iValues"][:3] == [145, 1, 28] and ms[29]["operands"]["sValues"][0] == "sc_e_0288", \
        "Rachel's house changed"
    wc_jump(sc, 1, 28, "wc_no_scene")
    out.append((sc, S.path("sc_e_0283")))

    # Strago's house the morning after the fire (sc_e_0539_3, map 186): WC cuts the wake-up scene;
    # its flags (ScenarioFlag2 19: Thamasa after the fire), the recovery, the map title, the save
    sc = S.load("sc_e_0539_3")
    ms = sc["Mnemonics"]
    assert ms[1]["operands"]["sValues"][0] == "ev_e_0539" and ms[6]["mnemonic"] == "FadeIn" and         ms[65]["operands"]["iValues"][0] == 19 and ms[70]["mnemonic"] == "ShowMapTitle", "Strago's house changed"
    S.hook(sc, 1, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla")] + S.copy_ops(sc, 65, 70)
           + S.copy_ops(sc, 6, 7) + S.copy_ops(sc, 70, 73))
    out.append((sc, S.path("sc_e_0539_3")))

    # Figaro Castle (sc_e_0028, map 27): until it's seen (ScenarioFlag1 16), talking to the priest
    # plays Edgar and Sabin's coin-toss flashback (sc_e_0028_1, map 28). In a WC game: her talk.
    sc = S.load("sc_e_0028")
    ms = sc["Mnemonics"]
    assert ms[1]["operands"]["iValues"][:3] == [16, 1, 7] and ms[6]["operands"]["sValues"][0] == "sc_e_0028_1" and \
        ms[8]["operands"]["sValues"][0] == "sc_npc_20051_6_1", "Figaro's priest changed"
    wc_jump(sc, 1, 7, "wc_no_scene")
    out.append((sc, S.path("sc_e_0028")))

    # Kefka in Vector's jail (sc_e_0498, map 171): walking up to his cell starts his monologue and
    # the dinner's timer (ScenarioFlag2 2: the dinner's talks, which WC games never start; on
    # time out it changes scripts). In a WC game: nothing (he stays in his cell).
    sc = S.load("sc_e_0498")
    ms = sc["Mnemonics"]
    assert ms[2]["operands"]["sValues"][0] == "タイマー表示" and ms[9]["mnemonic"] == "Msg", \
        "Kefka's jail changed"
    S.hook(sc, 1, "wc_no_scene", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Exit"),
    ])
    out.append((sc, S.path("sc_e_0498")))

    # Kohlingen (sc_e_0281, map 102): an old man walks up to the party and talks about Locke and
    # Rachel. In a WC game: nothing.
    sc = S.load("sc_e_0281")
    ms = sc["Mnemonics"]
    assert ms[2]["mnemonic"] == "Calc" and ms[12]["operands"]["sValues"][0] == "E0281_00_102_a_01", "Kohlingen's old man changed"
    S.hook(sc, 1, "wc_no_scene", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Calc", i=[1, 1], s=["MapLocalVariable", "←", "imm"]),   # as vanilla: once a visit
        S.op("Exit"),
    ])
    out.append((sc, S.path("sc_e_0281")))

    # Darill's tomb (the stairs Dullahan opens, sc_e_0700, map 468): vanilla moves Setzer to the
    # reserve and plays two flashbacks in the Falcon's hangar (map 577: sc_e_0700_1, then
    # sc_e_0701/sc_e_0702 by talking to a Setzer there) before the Falcon scene (sc_e_0703, where
    # this port gave the reward; the hangar has no way out). WC (event/daryl_tomb.py) gives the
    # reward after Dullahan and makes the stairs the way back to the World of Ruin, Falcon
    # scenes cut. In a WC game: the reward on the stairs (gate met, not done), then out to the
    # World of Ruin at the tomb.
    sc = S.load("sc_e_0700")
    ms = sc["Mnemonics"]
    assert ms[2]["operands"]["sValues"][0].endswith("セッツァー") and \
        ms[7]["operands"]["sValues"][0] == "sc_e_0700_1", "Darill's tomb stairs changed"
    S.hook(sc, 1, "wc_daryl_tomb", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["daryl_tomb"], 1, "@out"),
    ] + gate("daryl_tomb", "@out") + give("daryl_tomb") + [
        S.label("out"),
        S.op("ChangeMap", i=list(DARILL_EXIT)),
        S.op("Exit"),
    ])
    out.append((sc, S.path("sc_e_0700")))
    return out


def narshe_rooms():
    """Narshe's houses in a WC game (WC's start is after the opening: the town's regular WoB state;
    the PR keys it to story flags WC's start leaves off because they also start scenes: ScenarioFlag1
    194, after the Magitek Research Facility; 138). The four shops (sc_map_20011_6/8/9/10): their
    WoB stock (groups 44-47, the SNES shops WC randomizes), not the opening's (18-21: SNES shops
    WC calls inaccessible, never randomized). The treasure room (sc_map_20011_13): the locked
    chest and its owner (ev_e_0259; without it the chest drawn in the map had nothing there: no
    collision, no "It's locked.")."""
    out = []
    for name in ("sc_map_20011_6", "sc_map_20011_8", "sc_map_20011_9", "sc_map_20011_10"):
        sc = S.load(name)
        ms = sc["Mnemonics"]
        assert ms[1]["operands"]["iValues"][:3] == [194, 1, 6] and ms[6]["label"] and \
            ms[9]["operands"]["sValues"][0] == "ev_e_0259", f"Narshe shop {name} changed"
        S.hook(sc, 1, "wc_narshe_shop", [
            if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
            S.op("Jump", i=[6]),                    # after the Magitek Research Facility
        ])
        out.append((sc, S.path(name)))
    sc = S.load("sc_map_20011_13")
    ms = sc["Mnemonics"]
    assert ms[9]["operands"]["iValues"][:3] == [138, 0, 21] and ms[11]["operands"]["sValues"][0] == "ev_e_0259", \
        "Narshe treasure room changed"
    S.hook(sc, 9, "wc_treasure_room", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Jump", i=[9]),                        # on to its WoB / WoR entities
    ])
    out.append((sc, S.path("sc_map_20011_13")))
    return out


# Thamasa's shop rooms (map 20271: 1 item, 2 weapons + armor, 3 relics) and the code at which each
# picks its shopkeepers by ScenarioFlag2 12, with that branch's target (the working set)
THAMASA_SHOPS = (("sc_map_20271_1", 21, 25), ("sc_map_20271_2", 18, 22), ("sc_map_20271_3", 18, 22))


def thamasa_shops():
    """Thamasa's shops in a WC game (the user's Deck game, 2026-11-12: "shops in thamasa aren't
    talking to me"). The PR keys its shopkeepers to ScenarioFlag2 12, which only vanilla's landing
    near Thamasa with Leo sets (sc_e_0518_1, world map): before it the rooms hold stand-ins with no
    script (ev_e_0001), after it the shops (ev_e_0518: groups 48-51, the SNES shops WC randomizes).
    WC never plays that landing and the flag can't be a start flag (it also arms the town's arrival
    scenes, sc_map_20270's ev_e_0519/0520): in a WC game the rooms always take the shops. The
    same rooms serve the WoR."""
    out = []
    for name, at, target in THAMASA_SHOPS:
        sc = S.load(name)
        ms = sc["Mnemonics"]
        assert ms[at]["mnemonic"] == "Branch" and ms[at]["operands"]["iValues"][:3] == [12, 1, target] \
            and ms[at]["operands"]["sValues"][0] == "ScenarioFlag2" and ms[target]["label"] \
            and ms[target + 1]["operands"]["sValues"][0] == "ev_e_0518", f"Thamasa shop {name} changed"
        wc_jump(sc, at, target, label="wc_thamasa_shop")
        out.append((sc, S.path(name)))
    return out


def thamasa_strago_house():
    """Strago's house in Thamasa (sc_map_20271_5) in a WC game: nobody home. The same flag 12 as
    the shops: before it the PR places a Strago stand-in with no script (ev_e_0001, "Strago (before
    the event)"), which WC's game showed for good, silent, even with Strago in the party. WC's
    start clears his at-home NPC bit (event/burning_house.py: STRAGO_THAMASA_HOME), as vanilla's
    house is after the fire (16 set: no entity set): the WC branch goes to the script's end."""
    sc = S.load("sc_map_20271_5")
    ms = sc["Mnemonics"]
    assert ms[18]["mnemonic"] == "Branch" and ms[18]["operands"]["iValues"][:3] == [12, 1, 21] \
        and ms[19]["operands"]["sValues"][0] == "ev_e_0001" and ms[28]["label"] and ms[29]["mnemonic"] == "Exit", \
        "Strago's house changed"
    wc_jump(sc, 18, 28, label="wc_strago_house")
    return sc


def south_figaro_basement():
    """South Figaro's basement (sc_map_20071_12, map 50): WC's South Figaro is the town as it is
    after Locke's escape (event/south_figaro.py: not occupied; the start flags no longer set
    ScenarioFlag1 50, the occupation) with Celes still chained below (its npc bit). The PR places
    her only "during Locke's scenario" (50 set, 64 not): in a WC game, until she is freed."""
    sc = S.load("sc_map_20071_12")
    ms = sc["Mnemonics"]
    assert ms[1]["operands"]["iValues"][:3] == [50, 0, 11] and ms[2]["operands"]["iValues"][:3] == [64, 1, 11] \
        and ms[6]["operands"]["sValues"][0] == "ev_e_0079", "South Figaro basement changed"
    S.hook(sc, 1, "wc_basement", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Jump", i=[2]),                        # "during Locke's scenario": Celes until freed (59)
    ])
    return sc


def figaro_castle_map():
    """Figaro Castle's main WoB map (sc_map_20050): WC clears the SNES NPC bit shared by the
    castle guards during initialization. The PR models those two guards as the ev_e_0018_1 or
    ev_e_0026 entity layers instead; skip both layers in a WC game so they do not block the doors
    to the east and west wings (and therefore the shops)."""
    sc = S.load("sc_map_20050")
    ms = sc["Mnemonics"]
    assert ms[21]["operands"]["iValues"][:3] == [15, 1, 24] and \
        ms[22]["operands"]["sValues"][0] == "ev_e_0018_1" and \
        ms[26]["operands"]["sValues"][0] == "ev_e_0026" and ms[33]["label"], "Figaro castle changed"
    wc_jump(sc, 21, 32, "wc_no_guards")       # on to the next independent group at index 33
    wc_jump(sc, 72, 92, "wc_no_story")        # no Kefka/Edgar story triggers after the throne check
    return sc


def figaro_throne_map():
    """Figaro Castle's throne room in the WoB (sc_map_20051_13): Edgar's scene (ev_e_0029, the
    figaro_castle_wob check) while the check is open (vanilla: between Kefka's visit and the fire)."""
    sc = S.load("sc_map_20051_13")
    ms = sc["Mnemonics"]
    assert ms[5]["operands"]["iValues"][:3] == [14, 0, 13] and ms[12]["operands"]["sValues"][0] == "ev_e_0029", "throne changed"
    S.hook(sc, 5, "wc_throne", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["figaro_castle_wob"], 1, 13),
        S.op("SetEntities", s=["ev_e_0029"]),
        S.op("Jump", i=[13]),
    ])
    return sc


def thamasa_burning_map():
    """Thamasa (sc_Map_20270): the burning house's entrance (ev_e_0523, the burning_house check)
    while the check is open (vanilla: only during the fire, ScenarioFlag2 16 and not 17)."""
    sc = S.load("sc_Map_20270")
    ms = sc["Mnemonics"]
    assert ms[50]["operands"]["iValues"][:3] == [16, 0, 54] and ms[52]["operands"]["sValues"][0] == "ev_e_0523", "Thamasa changed"
    S.hook(sc, 50, "wc_fire", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(WC_FLAGS, CHECK_FLAGS["burning_house"], 1, 54),
        S.op("Jump", i=[51]),
    ])
    return sc


def esper_mountain_map():
    """The WoB's world map (sc_Map_10010): Esper Mountain's entrance open (vanilla: after
    ScenarioFlag2 19, which also turns Thamasa to after the fire)."""
    sc = S.load("sc_Map_10010")
    ms = sc["Mnemonics"]
    assert ms[90]["operands"]["iValues"][:3] == [19, 1, 93] and ms[91]["operands"]["sValues"][0] == "ev_e_0539", "world map changed"
    wc_jump(sc, 90, 93)
    return sc


def kt_skip():
    """WC's "Unlock KT Skip" objective result (event/kefka_tower.py's landing menu): after the
    three parties are picked for Kefka's Tower (sc_e_0966_3_1, before the landing), a WC game
    whose KT skip objective is complete (and whose final Kefka is open: WC:kt_skip) may go straight
    to the final stairway (sc_e_1002, map 570; the final room works out who's there itself). Also
    no forced Celes lead in a WC game (she may not be recruited)."""
    sc = S.load("sc_e_0966_3_1")
    ms = sc["Mnemonics"]
    assert ms[6]["mnemonic"] == "OrganizePartySlot" and ms[7]["operands"]["sValues"][0].startswith("パーティ編成") \
        and ms[11]["operands"]["sValues"][0] == "sc_e_0966_3", "KT formation changed"
    skip_at(sc, 6)
    S.hook(sc, 11, "wc_kt_skip", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("SysCall", s=["WC:kt_skip"]),
        if_flag(WC_FLAGS, SCRATCH, 0, "@vanilla"),
        S.op("FadeIn", i=[0, 0, 0, 255], r=[0.7]),
        S.op("Msg", s=["WC_KT_ASK"]),
        S.op("Select", s=["WC_KT_YES", "WC_KT_NO"]),
        selection(1, "@climb"),
        S.op("FadeOut", i=[0, 0, 0, 255], r=[0.7]),
        S.op("ChangeMap", i=[570, 1, 0, 0, 1], s=["sc_e_1002"]),
        S.op("Exit"),
        S.label("climb"),
        S.op("FadeOut", i=[0, 0, 0, 255], r=[0.7]),
        S.op("Jump", i=["@vanilla"]),
    ])
    return sc


def final_kefka():
    """WC's "Unlock Final Kefka" objective result (event/kefka_tower.py: the final switches send
    the party back until it's complete): the stairway into Kefka's room (sc_e_1002 in map 570,
    ChangeMap 571) only once WC:final_kefka finds the objective complete (or the seed has none)."""
    f = [f for f in S.all_paths("sc_e_1002") if "Map_30871_28" in f]
    assert len(f) == 1, "final stairway not found"
    sc = S.load_path(f[0])
    ms = sc["Mnemonics"]
    assert ms[17]["mnemonic"] == "ChangeMap" and ms[17]["operands"]["iValues"][0] == 571, "final stairway changed"
    S.hook(sc, 1, "wc_final", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("SysCall", s=["WC:final_kefka"]),
        if_flag(WC_FLAGS, SCRATCH, 1, "@vanilla"),
        S.op("Msg", s=["WC_REWARD"]),
        S.op("Exit"),
    ])
    return [(sc, f[0])]


def auction(name, announce, se, got, item, n, check):
    """Jidoor's auction (WC event/auction_house.py; map 111): the sale of an esper (`name`)
    announces and gives the seed's n-th reward instead (the vanilla bidding and prices)."""
    sc = S.load(name)
    ms = sc["Mnemonics"]
    key = ms[announce]["operands"]["sValues"][0]
    assert ms[announce]["mnemonic"] == "Msg" and key.endswith("_00_111_a_01") and ms[se]["mnemonic"] == "PlaySE" \
        and ms[got]["mnemonic"] == "Msg" and ms[item]["mnemonic"] == "GetItem", f"{name} changed"
    S.hook(sc, announce, "wc_announce", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("SysCall", s=[f"WC:announce auction_house {n} {key}"]),
        S.op("Jump", i=["@vanilla"]),
    ])
    S.hook(sc, se, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + give("auction_house", n=n, check=check) + [S.op("Jump", i=[got])])
    skip_at(sc, item)
    return sc


def auction_golem():
    """The Golem's sale (sc_e_0422: the auction's start, its random pick, the Golem): WC's reward2.
    At the start, WC's -adeh hint: the greeting every sale opens with (E0422_99_111_a_01) says
    whether an esper is still for sale."""
    sc = auction("sc_e_0422", 37, 84, 85, 89, n=1, check="auction_2")
    ms = sc["Mnemonics"]
    assert ms[2]["mnemonic"] == "Branch" and ms[2]["operands"]["iValues"][0] == 203, "auction start changed"
    # WC's -anca (no talking chocobo / 1/1200 airship sales): the random pick (map-local 2) draws
    # from one fewer and the last value becomes the next sale's: WoB 1-4 (4 -> 5, the Cure Ring;
    # the Talkobo is 4), WoR 1-5 (5 -> 6, the imp robot; the airship is 5). The same odds as WC's
    # re-roll, without a loop
    for at, count, sale in ((8, 5, 4), (15, 6, 5)):
        assert ms[at]["mnemonic"] == "SetRandomValue" and ms[at]["operands"]["iValues"][:2] == [2, count], "auction picks changed"
        S.hook(sc, at, f"wc_anca_{at}", [
            if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
            S.op("SysCall", s=["WC:hasflag -anca"]),
            if_flag(WC_FLAGS, SCRATCH, 0, "@vanilla"),
            S.op("SetRandomValue", i=[2, count - 1], s=["MapLocalVariable"]),
            S.op("Branch", i=[2, sale, "@last"], s=["MapLocalVariable", EQ, "imm"]),
            S.op("Jump", i=[at]),
            S.label("last"),
            S.op("Calc", i=[2, count], s=["MapLocalVariable", "←", "imm"]),
            S.op("Jump", i=[at]),
        ])
    S.hook(sc, 2, "wc_hint", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("SysCall", s=[f"WC:auction_hint E0422_99_111_a_01 {CHECK_FLAGS['auction_1']} {CHECK_FLAGS['auction_2']}"]),
        S.op("Jump", i=["@vanilla"]),
    ])
    return sc


def auction_zona_seeker():
    """The Zona Seeker's sale (sc_e_0423): WC's reward1."""
    return auction("sc_e_0423", 14, 77, 78, 82, n=0, check="auction_1")


# the 8 dragons in WC's reward order: (check, PR "defeated" flag, ScenarioFlag2)
DRAGONS = [("narshe_dragon", 138), ("mt_zozo_dragon", 108), ("opera_house_dragon", 178), ("kefka_tower_dragon_g", 225),
           ("kefka_tower_dragon_s", 220), ("ancient_castle_dragon", 175), ("phoenix_cave_dragon", 148),
           ("fanatics_tower_dragon", 166)]


def dragons():
    """WC's 8 dragons (event/eight_dragons.py): every dragon's scene ends in the countdown
    (sc_e_0975, a copy in each dragon's map), which first gives the seed's reward of each dragon
    defeated (its flag) and not yet rewarded, instead of the countdown (and the Crusader after
    the eighth: WC has neither)."""
    out = []
    for f in S.all_paths("sc_e_0975"):
        sc = S.load_path(f)
        ms = sc["Mnemonics"]
        flags = [ms[i]["operands"]["iValues"][0] for i in range(2, 31, 4)]
        assert sorted(flags) == sorted(fl for _, fl in DRAGONS) and ms[2]["mnemonic"] == "Branch", f"{f}: countdown changed"
        ops = [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla")]
        for i, (check, fl) in enumerate(DRAGONS):
            ops += [
                if_flag("ScenarioFlag2", fl, 0, f"@d{i}"),
                if_flag(WC_FLAGS, CHECK_FLAGS[check], 1, f"@d{i}"),
            ] + give("eight_dragons", n=i, check=check, se=False) + [S.label(f"d{i}")]
        # WC drops the countdown and the Crusader after the eighth (memory/free.py 0xc1f9f-0xc2047)
        ops.append(S.op("Exit"))
        S.hook(sc, 2, "wc_dragons", ops)
        out.append((sc, f))
    return out


def ebots_rock_start(name):
    sc = S.load(name)
    gate_at(sc, 1, "ebots_rock")
    return sc


def ebots_rock_fight_a():
    """Ebot's Rock (WC event/ebots_rock.py, gate Strago; map 540): Hidon only with the gate met
    (two versions of the fight)."""
    return ebots_rock_start("sc_e_0950")


def ebots_rock_fight_b():
    return ebots_rock_start("sc_e_0952")


def ebots_rock_a():
    """After Hidon, first version (sc_e_0950_1, Strago cheering): the seed's reward first."""
    sc = S.load("sc_e_0950_1")
    ms = sc["Mnemonics"]
    assert ms[6]["operands"]["sValues"][0] == "E0950_00_540_a_01", "Ebot's Rock a changed"
    assert ms[11]["operands"]["sValues"][0] == "sc_e_0950_2" and ms[10]["mnemonic"] == "FadeOut", "after Hidon changed"
    # the reward instead of Strago's cheering (WC: "strago celebrating after hidon defeated"), on
    # at the fade out (10)
    reward_at(sc, 6, "ebots_rock", then=10)
    # then none of the Thamasa scenes vanilla chains after it (sc_e_0950_2-5: Gungho hurt, the
    # dinner's story, Relm looking for Strago; no flags): straight to their end in Strago's house
    # (sc_e_0950_6, map 310: ScenarioFlag2 205, the Falcon outside Thamasa, the save)
    S.hook(sc, 11, "wc_no_scene", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("ChangeMap", i=[310, 1, 0, 0, 1], s=["sc_e_0950_6"]),
        S.op("Exit"),
    ])
    return sc


def ebots_rock_b():
    """After Hidon, second version (sc_e_0952_1): the seed's reward before its done flag (ScenarioFlag2 207)."""
    sc = S.load("sc_e_0952_1")
    ms = sc["Mnemonics"]
    assert ms[3]["mnemonic"] == "SetFlag" and ms[3]["operands"]["iValues"][0] == 207, "Ebot's Rock b changed"
    reward_at(sc, 3, "ebots_rock", then="keep")
    return sc


def ebots_rock_evening():
    """After Hidon, the bedroom card on the way to the dinner (sc_e_0950_3: the room fades in,
    "That evening...", out to sc_e_0950_4). WC cuts the bedroom scene and "that evening" (its
    event/ebots_rock.py: straight to the dinner table, the party in Strago's place); its
    narration was the one story line a WC game still showed in a check's scenes (the text
    audit, 2026-11-11). In a WC game: from the tint straight to the dinner, on black."""
    sc = S.load("sc_e_0950_3")
    ms = sc["Mnemonics"]
    assert ms[4]["mnemonic"] == "FadeIn" and ms[6]["operands"]["sValues"][0] == "E0950_02_305_a_01"         and ms[9]["mnemonic"] == "ChangeMap" and ms[9]["operands"]["sValues"][0] == "sc_e_0950_4", "Ebot's Rock evening changed"
    S.hook(sc, 4, "wc_no_evening", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Jump", i=[8]),                                   # on at 9: the dinner
    ])
    return sc


def burning_house_start():
    """Thamasa's burning house (WC event/burning_house.py, gate Strago): going in (sc_e_0523, map
    180) only with the gate met."""
    sc = S.load("sc_e_0523")
    ms = sc["Mnemonics"]
    assert ms[2]["mnemonic"] == "HidePlayer" and ms[116]["mnemonic"] == "FadeOut" and         ms[118]["operands"]["iValues"][0] == 17 and ms[122]["operands"]["iValues"][:2] == [412, 1], "Thamasa's fire changed"
    gate_at(sc, 1, "burning_house")
    # WC (burning_house.py "burning house entrance event") skips Strago's plea, the townsfolk's
    # magic and his join: straight into the house with the fire's flag (ScenarioFlag2 17)
    S.hook(sc, 2, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla")] + S.copy_ops(sc, 116, 117)
           + S.copy_ops(sc, 118, 119) + S.copy_ops(sc, 122, 124))
    return sc


def burning_house():
    """After the Flame Eater (sc_e_0539_1, map 420): the seed's reward before the scene moves on to
    Strago's house."""
    sc = S.load("sc_e_0539_1")
    ms = sc["Mnemonics"]
    assert ms[50]["mnemonic"] == "ChangeMap" and ms[50]["operands"]["iValues"][0] == 186, "Flame Eater changed"
    assert ms[47]["mnemonic"] == "FadeOut", "Flame Eater fade moved"
    assert ms[2]["mnemonic"] == "HidePlayer" and ms[3]["mnemonic"] == "FadeIn", "after the Flame Eater changed"
    # WC ("flame eater defeated", "burning house wake up") cuts the rescue and the morning after:
    # the reward on black, then Strago's house (sc_e_0539_3, trimmed too: story_scenes)
    S.hook(sc, 2, "wc_no_scene", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"), S.op("Jump", i=[49])])
    reward_at(sc, 50, "burning_house", then="keep", dark=True)
    return sc


def mines_1f_map():
    """Narshe's mines 1F (sc_map_30021_1, maps 323/632): ScenarioFlag1 13 (the opening's mine
    march over) gates three sets: the opening's Magitek walls (ev_e_0001: 64 colliders), the
    mine door (ev_e_0010) and the Whelk (ev_e_0011). A WC game leaves 13 clear so the Whelk check
    loads here, which left the walls up too: they block the bridge before the east tunnel's
    mouth (320,384; the tunnel to map 634/325) and the save point's side (the user's Deck,
    2026-11-12: "i can't go through that mouth"). In a WC game the walls stay out."""
    return skip("sc_map_30021_1", 18, "SetEntities ev_e_0001", "the opening's Magitek walls")


def whelk_start():
    """Narshe's mines (WC event/whelk.py, gate Terra): the Whelk only with the gate met."""
    sc = S.load("sc_e_0011")
    gate_at(sc, 1, "whelk")
    return sc


def whelk():
    """After the Whelk (sc_e_0011_1): the seed's reward before the done flag (ScenarioFlag1 9)."""
    sc = S.load("sc_e_0011_1")
    ms = sc["Mnemonics"]
    assert ms[4]["mnemonic"] == "SetFlag" and ms[4]["operands"]["iValues"][0] == 9, "Whelk changed"
    reward_at(sc, 4, "whelk", then="keep")
    return sc


def magitek_pickup(name, msg_key):
    """The Magitek Factory's Ifrit / Shiva magicite (WC event/magitek_factory.py, gate Celes; map
    398): either one gives the factory's first reward and takes both (ScenarioFlag1 189, 190)."""
    sc = S.load(name)
    ms = sc["Mnemonics"]
    assert ms[6]["operands"]["sValues"][0] == msg_key and ms[8]["mnemonic"] == "GetItem", f"{name} changed"
    gate_at(sc, 1, "magitek_factory")
    S.hook(sc, 6, "wc_reward", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + give("magitek_factory", [("ScenarioFlag1", 189), ("ScenarioFlag1", 190)]) + [
        S.op("Exit"),
    ])
    return sc


def magitek_ifrit():
    return magitek_pickup("sc_e_0399", "E0399_00_398_a_01")


def magitek_shiva():
    return magitek_pickup("sc_e_0400", "E0400_00_398_a_02")


def owzer_mansion():
    """Owzer's Mansion (WC event/owzer_mansion.py; map 446): after Chadarnook, Relm and Owzer
    (sc_e_0775): the seed's reward instead of Lakshmi + Relm (ScenarioFlag2 135 = done)."""
    return talk_reward("sc_e_0775", 1, "owzer_mansion", [("ScenarioFlag2", 135)], expect="PlayBGM")


def zone_eater():
    """Inside the Zone Eater (WC event/zone_eater.py; map 507): Gogo (sc_e_0865): the seed's reward
    instead of Gogo (ScenarioFlag2 154); the stand-in (entity 1, ev_e_0865) is gone after it."""
    return talk_reward("sc_e_0865", 1, "zone_eater", [("ScenarioFlag2", 154)], expect="BranchByPlayerDirection",
                       hide=(1,))


def mt_zozo():
    """Mt. Zozo (WC event/mt_zozo.py; map 474): Cyan's room (sc_e_0735, the start of the letters
    scene): the seed's reward instead of the scene and Cyan (ScenarioFlag2 109); once done, the
    vanilla door into the room without the scene."""
    return talk_reward("sc_e_0735", 1, "mt_zozo", [("ScenarioFlag2", 109)], expect="Branch", after_state=True)


def umaro_cave():
    """Umaro's cave (WC event/umaro_cave.py; map 496): after the battle, talking to Umaro
    (sc_e_0790): the seed's reward instead of Umaro (no Mog needed, as WC; ScenarioFlag2 142);
    the stand-in (entity 1, ev_e_0790) is gone after it."""
    return talk_reward("sc_e_0790", 1, "umaro_cave", [("ScenarioFlag2", 142)], expect="BranchByCharacter",
                       hide=(1,))


def umaro_cliff():
    """The jump into Umaro's cave (sc_e_0781, Tritoch's cliff, map 20; WC event/umaro_cave.py
    add_gating_condition, gate Umaro): with the gate closed WC shows only "There's an opening
    in the cliff." and walks the party back (CLIFF_MOVE_BACK), no jump offered. Not tied to the
    check being done: WC lets the party jump again afterwards. (Gated only at the reward before,
    after the cave and Umaro's battle: the user's Deck playtest, 2026-11-12.)"""
    sc = S.load("sc_e_0781")
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "Msg" and ms[1]["operands"]["sValues"][0] == "E0781_00_020_a_01" \
        and ms[11]["mnemonic"] == "Call" and ms[11]["operands"]["iValues"][0] == 27 \
        and ms[27]["label"] == "return_move", "Umaro's cliff changed"
    S.hook(sc, 1, "wc_gate", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
    ] + gate("umaro_cave", "@closed") + [
        S.op("Jump", i=["@vanilla"]),
        S.label("closed"),
        S.op("Msg", s=["WC_UMARO_CLIFF"]),
        S.op("Call", i=[27]),                 # return_move: the party steps back from the edge
        S.op("Exit"),
    ])
    return sc


def umaro_carving_line():
    """The bone carving (sc_e_0789): no "Obtained Midgardsormr magicite!" in a WC game (WC puts
    the esper in its reward pool); the battle with Umaro still follows."""
    return skip("sc_e_0789", 22, "Msg E0789_00_496_a_04", "Midgardsormr's line")


def umaro_carving_item():
    """After that battle (sc_e_0789_1): no Midgardsormr magicite in a WC game."""
    return skip("sc_e_0789_1", 3, "GetItem", "Midgardsormr")


def tzen_map():
    """Tzen (WoB): the seller is there in a WC game (WC sets ESPER_SELLER_TZEN at the start; the
    PR's flag for him, ScenarioFlag1 203, is a big story flag tested in 13 places)."""
    sc = S.load("sc_map_20190")
    ms = sc["Mnemonics"]
    assert ms[4]["mnemonic"] == "Branch" and ms[4]["operands"]["iValues"][:3] == [203, 0, 6] and ms[6]["label"], "Tzen map changed"
    S.hook(sc, 4, "wc_tzen_seller", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("SetEntities", s=["ev_e_0427"]),
        S.op("Jump", i=[6]),
    ])
    return sc


# WC's quests (constants/objectives/condition_bits.py quest_bit): done flags ScenarioFlag4 70 + the
# plugin's index (WcTrack.QuestNames). Story quests the PR has no flag for get one here.
QUEST_FLAG = 70
QUEST_CHECKPOINT, QUEST_OPERA, QUEST_ZOZO_CLOCK = 3, 4, 6
CHECKPOINT_STARTED = 85    # Narshe's light-path trial under way


def quest_checkpoint_start():
    """"Pass Security Checkpoint": Narshe's light-path trial (map 320) starts in sc_e_0090 (its
    map-local step counter set to 1). A failure warps back to the south side.
    WC keeps the trial but deletes its explanation (event/narshe_wob.py security_checkpoint_mod:
    CC/DA09 returns; the party gathering and Edgar's line, dialog 0x1AC). Here, in a WC game:
    the route of light still shows, Edgar's and Terra's three lines don't (ScenarioFlag1 68's
    "already explained" path; the user's playtest, 2026-11-06)."""
    sc = S.load("sc_e_0090")
    ms = sc["Mnemonics"]
    assert ms[17]["mnemonic"] == "Calc" and ms[17]["operands"]["sValues"][0] == "MapLocalVariable", "checkpoint start changed"
    assert ms[6]["mnemonic"] == "Branch" and ms[6]["operands"]["iValues"][:3] == [68, 1, 16] and ms[16]["label"] \
        and ms[10]["operands"]["sValues"][0] == "E0090_00_320_a_01", "checkpoint explanation changed"
    S.hook(sc, 6, "wc_no_explanation", [if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"), S.op("Jump", i=[16 - 1])])
    S.hook(sc, 17, "wc_checkpoint_start", [flag(WC_FLAGS, CHECKPOINT_STARTED)])
    return sc


def quest_checkpoint_south():
    """The trial's south side (map 321, sc_Map_30011_5): a failure (or leaving) ends the attempt."""
    sc = S.load("sc_Map_30011_5")
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "ResetFlag" and ms[1]["operands"]["iValues"][0] == 46, "checkpoint south changed"
    S.hook(sc, 1, "wc_checkpoint_south", [reset(WC_FLAGS, CHECKPOINT_STARTED)])
    return sc


def quest_checkpoint_north():
    """The trial's north side (map 317, sc_Map_30011_1) reached with the trial under way: passed."""
    sc = S.load("sc_Map_30011_1")
    ms = sc["Mnemonics"]
    assert ms[2]["mnemonic"] == "ResetFlag" and ms[2]["operands"]["iValues"][0] == 46, "checkpoint north changed"
    S.hook(sc, 2, "wc_checkpoint_north", [
        if_flag(WC_FLAGS, CHECKPOINT_STARTED, 0, "@vanilla"),
        flag(WC_FLAGS, QUEST_FLAG + QUEST_CHECKPOINT),
        reset(WC_FLAGS, CHECKPOINT_STARTED),
    ])
    return sc


# WC's Zozo clock clues (event/zozo.py randomize_clock_mod's dialog ids) -> the PR's NPC lines
ZOZO_CLUES = {1046: "N008_C00_390_06_01", 1047: "N008_C00_390_02_01", 1048: "N008_C00_390_03_01",
              1049: "N008_C00_390_04_01", 1050: "N008_C00_390_05_01", 1057: "N008_C00_384_01_01",
              1058: "N008_C00_387_01_01", 1066: "N002_C00_387_02_01", 1059: "N008_C00_383_05_01",
              1061: "N008_C00_391_01_01"}


def wc_text(text):
    """WC dialog markup as PR message text: <line> a line break, <end> dropped."""
    return text.replace("<end>", "").replace("<line>", "\\n").strip()


def quest_zozo_clock():
    """The Zozo clock (sc_e_0299). WC randomizes its time (-rc, always on): each hand's menu
    (hour, minute, second; the PR's options are WC's order) asks the plugin whether the choice is
    the seed's (WC:clock <hand>: ScenarioFlag4 SCRATCH), right -> the hand's counter, wrong -> the
    next hand; 3 right -> the reward. "Set Zozo Clock" (the quest): its right answer (after the
    label at 30)."""
    sc = S.load("sc_e_0299")
    ms = sc["Mnemonics"]
    assert ms[30]["label"] and ms[31]["mnemonic"] == "Call", "Zozo clock changed"
    # (first Branch of the hand, its counter Calc, the next hand's label)
    hands = [(4, 9, 10), (13, 17, 18), (21, 25, 26)]
    for hand, (at, calc, nxt) in enumerate(hands):
        assert ms[at - 1]["mnemonic"] == "Select" and ms[calc]["mnemonic"] == "Calc" and ms[nxt]["label"], "Zozo clock changed"
        S.hook(sc, at, f"wc_clock_{hand}", [
            if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
            S.op("SysCall", s=[f"WC:clock {hand}"]),
            if_flag(WC_FLAGS, SCRATCH, 0, nxt),       # wrong: on to the next hand
            S.op("Jump", i=[calc - 1]),               # right: the hand's counter
        ])
    S.hook(sc, 31, "wc_zozo_clock", [flag(WC_FLAGS, QUEST_FLAG + QUEST_ZOZO_CLOCK)])
    return sc


def opera_performance():
    """WC's opera performance (event/opera_house_wob.py), the "Perform In Opera" quest: playable in
    the WoB until done, not while the disruption runs; failing it is game over; done -> the quest.
    The PR: the stage trigger ev_e_0318 (sc_Map_20161_1, placed while ScenarioFlag1 159 is set and
    165, performed, isn't), the performance sc_e_0318_1 (the PR's opera system; map-local 7 = 1 on
    a failure), its success sc_e_0320_1. The impresario (sc_npc_20161_2_1) starts the disruption
    (sc_e_0330, the opera check) once the letter is read (166): in a WC game without waiting for
    the performance (165)."""
    out = []
    sc = S.load("sc_Map_20161_1")
    ms = sc["Mnemonics"]
    assert ms[26]["operands"]["iValues"][:3] == [161, 1, 29] and ms[42]["label"], "opera stage map changed"
    S.hook(sc, 26, "wc_opera_stage", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        if_flag(*COLLAPSED, 1, 42),                 # not in the WoR
        if_flag("ScenarioFlag1", 177, 1, "@vanilla"),   # the disruption over: open again
        if_flag("ScenarioFlag1", 167, 1, 42),       # the disruption running: no stage
        if_flag("ScenarioFlag1", 174, 1, 42),
    ])
    out.append((sc, S.path("sc_Map_20161_1")))
    sc = S.load("sc_npc_20161_2_1")
    ms = sc["Mnemonics"]
    assert ms[2]["operands"]["iValues"][:3] == [165, 1, 17] and ms[17]["label"], "impresario changed"
    S.hook(sc, 2, "wc_impresario", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Jump", i=[17]),                       # on to the disruption (next2: the letter read)
    ])
    out.append((sc, S.path("sc_npc_20161_2_1")))
    sc = S.load("sc_e_0318_1")
    ms = sc["Mnemonics"]
    assert ms[11]["label"] and ms[12]["mnemonic"] == "ChangePartySlot" and ms[29]["label"] and \
        ms[30]["operands"]["sValues"][0] == "sc_e_0328_1", "opera performance changed"
    S.hook(sc, 12, "wc_opera_failed", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("Jump", i=[29]),                       # game over (no retries, no Locke/Celes joining)
    ])
    out.append((sc, S.path("sc_e_0318_1")))
    sc = S.load("sc_e_0320_1")
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "ChangePartySlot" and ms[2]["mnemonic"] == "RemoveCondition" and ms[6]["mnemonic"] == "FadeIn", \
        "opera success changed"
    fade = ms[6]
    S.hook(sc, 1, "wc_opera_done", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("RemoveCondition", i=ms[2]["operands"]["iValues"][:2]),
        flag("ScenarioFlag1", 165),
        flag(WC_FLAGS, QUEST_FLAG + QUEST_OPERA),
        S.op("FadeIn", i=fade["operands"]["iValues"][:4], r=fade["operands"]["rValues"][:1]),
        S.op("Exit"),
    ])
    out.append((sc, S.path("sc_e_0320_1")))
    return out


def bum_rush_last():
    """WC's -brl (Bum Rush last): Duncan's scene (sc_e_0954, where the PR teaches it) waits until
    a Blitz holder knows the other seven (WC:brl answers; without -brl always): it ends before it
    starts and stays for a later visit (its done flag, ScenarioFlag2 208, is set at its end)."""
    sc = S.load("sc_e_0954")
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "StopBGM" and ms[151]["operands"]["sValues"][0].endswith("夢幻闘舞"), "Duncan's scene changed"
    S.hook(sc, 1, "wc_bum_rush", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("SysCall", s=["WC:brl"]),
        if_flag(WC_FLAGS, SCRATCH, 1, "@vanilla"),
        S.op("Exit"),
    ])
    return sc


DEMON_BEATEN = ("ScenarioFlag2", 238)   # Kefka's Tower's Demon statue beaten (sc_e_0994_1; WC's Poltergeist)


def kt_demon_drop():
    """WC's -fbs (both presets): the ledge behind Kefka's Tower's Demon statue (map 564: in through
    the door at (36, 25), the drop at (36, 28), sc_e_0968) is a way past the statue, which WC blocks
    until its Poltergeist (the PR's Demon) is beaten (event/kefka_tower.py poltergeist_skip_fix; the
    Inferno one needs nothing: the PR's walls leave no way round its trigger). In a WC game with
    -fbs, before that, the drop sets the party back a tile instead."""
    # (the main tower map has a ledge script of the same name: map 564's only)
    f = [f for f in S.all_paths("sc_e_0968") if "Map_30871_22" in f]
    assert len(f) == 1, "Kefka's Tower ledge not found"
    sc = S.load_path(f[0])
    ms = sc["Mnemonics"]
    assert ms[1]["mnemonic"] == "Call" and ms[3]["label"] == "move_f", "Kefka's Tower ledge changed"
    S.hook(sc, 1, "wc_fbs", [
        if_flag(WC_FLAGS, WC_GAME, 0, "@vanilla"),
        S.op("SysCall", s=["WC:has -fbs"]),
        if_flag(WC_FLAGS, SCRATCH, 0, "@vanilla"),
        if_flag(*DEMON_BEATEN, 1, "@vanilla"),
        S.op("Call", i=["@back"]),
        S.op("Exit"),
    ] + puppets("back", (-1001, 0), lambda k: [S.op("Move", i=[0, 1], kind=2)]))
    return [(sc, f[0])]


SCRIPTS = [opening, blackjack_helm, falcon_helm, tzen_wob, tzen_wor, tzen_map, ancient_castle, ancient_castle_room,
           gau_father_house_map, gau_father_house, kohlingen_map, kohlingen, tritoch, zozo_map, zozo,
           owzer_mansion, zone_eater, mt_zozo, umaro_cliff, umaro_cave, umaro_carving_line, umaro_carving_item,
           esper_mountain_start, esper_mountain, figaro_castle_wor_start, figaro_castle_wor,
           collapsing_house_map, collapsing_house, narshe_wor, magitek_ifrit, magitek_shiva,
           doma_wor, doma_wor_throne, mobliz_wor_start, mobliz_wor, mobliz_fenrir, doom_gaze_start, doom_gaze,
           magitek_tubes, magitek_escape, lone_wolf, lone_wolf_hairpin, daryl_tomb, baren_falls_jump, baren_falls, south_figaro,
           figaro_castle_wob, mt_kolts_start, mt_kolts, magitek_024_start, magitek_024, doma_stooges, doma_stooges_lines, veldt, veldt_world, veldt_battle, ultima_intros, ultros_battles,
           fanatics_tower, fanatics_tower_dragon, magimaster, dragons, auction_golem, auction_zona_seeker, kefka_tower_atma, final_kefka,
           fc_airship_trip, fc_arrive, fc_airship_return, fc_beast, fc_statues, fc_escape, veldt_cave_start, veldt_cave_shadow,
           veldt_cave_relm, lete_river_start, lete_river, sealed_gate_start, sealed_gate, sealed_gate_after,
           sealed_gate_map, serpent_trench_start, serpent_trench_cave, serpent_trench_cliff, serpent_trench, nikeah_ferry,
           phantom_train_start, phantom_train, south_figaro_cave_map, south_figaro_cave_start, south_figaro_cave,
           doma_siege_start, doma_siege, phoenix_cave_map, phoenix_cave, narshe_battle_map, narshe_lineups,
           narshe_battle, opera_scripts, camp_scripts, moogle_defense_map, moogle_defense_ask, moogle_defense_party,
           moogle_defense, event_timers, blackjack_deck, narshe_town, figaro_castle_map, figaro_throne_map, figaro_shops, thamasa_burning_map,
           esper_mountain_map, kt_skip, kt_demon_drop, ebots_rock_fight_a, ebots_rock_fight_b, ebots_rock_a, ebots_rock_b, ebots_rock_evening,
           burning_house_start, burning_house, mines_1f_map, whelk_start, whelk,
           quest_checkpoint_start, quest_checkpoint_south, quest_checkpoint_north, quest_zozo_clock, bum_rush_last,
           opera_performance, narshe_rooms, thamasa_shops, thamasa_strago_house, south_figaro_basement,
           story_scenes, phantom_train_scenes, doma_dream, tower_scenes, wor_scenes]


# Lines WC removes that the PR gives no speaker (the user's Deck, 2026-11-12: "i'm gettting
# dialogue on the three stooges during doma dream and other places i shouldn't be getting
# ddialogue"). The plugin's story rule (WcEvents.SkipStoryLine) skips a line a character speaks
# and keeps one with no speaker as narration, but the PR leaves the speaker off many party and
# monster lines. tools/wc_scene_audit.py listed every speakerless line a WC game can reach; each
# below was matched to its SNES dialog and found inside code WC removes (its event module's
# Reserve(..., field.NOP()) or a scene it branches past). Kept as WC keeps them: questions and
# their answers (the moogle defense's optional three-parties explanation), signs and letters,
# puzzle text, and lines WC leaves in (Lone Wolf's "That's far enough!", the Phantom Train's
# "We'll have to detach the rear cars!", the collapsing house's "Please help him!", the burning
# house's "Huh!?", Thamasa's children, Gau's father's stove, Vector's guard, Kefka's Tower's
# weights, its three-parties tip).
WC_REMOVED_LINES = [
    # (script, line, WC's event module: the removed dialog)
    ("sc_e_0939", "E0939_00_537_a_01", "doma_wor.py: you must be wrexsoul"),
    ("sc_e_0792_1", "E0792_00_268_a_01", "phoenix_cave.py: the landing skips All right. We're going in!"),
    ("sc_e_0966_1", "E0792_00_268_a_01", "kefka_tower.py: kefka tower first landing scene"),
    ("sc_e_0966_4", "E0966_01_542_a_01", "kefka_tower.py: kefka tower the statues are up ahead"),
    ("sc_e_1002_1", "E1002_00_571_a_01", "kefka_tower.py: kefka tower welcome, friends"),
    ("sc_e_1002_1", "E1002_00_571_a_02", "kefka_tower.py: i knew you'd make it here"),
    ("sc_e_1002_1", "E1002_00_571_a_03", "kefka_tower.py: ultimate power, each party member's joy"),
    ("sc_e_1002_1", "E1002_00_571_a_07", "kefka_tower.py: ultimate power, each party member's joy"),
    ("sc_e_1002_1", "E1002_00_571_a_09", "kefka_tower.py: ultimate power, each party member's joy"),
    ("sc_e_1002_1", "E1002_00_571_a_10", "kefka_tower.py: ultimate power, each party member's joy"),
    ("sc_e_1002_1", "E1002_00_571_a_12", "kefka_tower.py: ultimate power, each party member's joy"),
    ("sc_e_1002_1", "E1002_00_571_a_27", "kefka_tower.py: kefka tower now for my next trick"),
    ("sc_e_1002_3", "E1002_00_571_a_30", "kefka_tower.py: kefka tower wor attack and pillar scenes"),
    ("sc_e_1002_3", "E1002_00_571_a_32", "kefka_tower.py: kefka tower wor attack and pillar scenes"),
    ("sc_e_1002_5", "E1002_00_571_a_34", "kefka_tower.py: shaking screen, its over kefka"),
    ("sc_e_0741", "E7041_00_488_a_01", "veldt_cave_wor.py: you're coming with us"),
    ("sc_e_0744_1", "E7044_00_492_a_02", "veldt_cave_wor.py: look at those wounds"),
    ("sc_e_0745_1", "E7044_00_492_a_02", "veldt_cave_wor.py: look at those wounds"),
    ("sc_e_0789", "E0789_00_496_a_01", "umaro_cave.py: what's with this carving"),
    ("sc_e_0890", "E0890_00_519_a_01", "ancient_castle.py: even the queen was turned to stone"),
    ("sc_e_0890", "E0890_00_519_a_02", "ancient_castle.py: a tear comes from the stone"),
    ("sc_e_0890", "E0890_00_519_a_03", "ancient_castle.py: a tear comes from the stone"),
    ("sc_e_0539", "E0539_00_420_a_01", "burning_house.py: approach flame eater dialog"),
    ("sc_e_0735_1", "E0735_00_475_a_01", "mt_zozo.py: mountain view dialog"),
    ("sc_e_0735_1", "E0735_00_475_a_02", "mt_zozo.py: CYAN!! dialog"),
    ("sc_e_0735_1", "E0735_00_475_a_04", "mt_zozo.py: cyan runs back to room"),
    ("sc_e_0773", "E0773_00_446_a_02", "owzer_mansion.py: help that painting!!"),
    ("sc_e_0773", "E0773_00_446_a_04", "owzer_mansion.py: help that painting!!"),
    ("sc_e_0206", "E0206_00_356_a_01", "phantom_train.py: ghosts leave party event before the last two cars"),
    ("sc_e_0206", "E0206_00_356_a_02", "phantom_train.py: ghosts leave party event before the last two cars"),
    ("sc_e_0214", "E0214_00_356_a_01", "phantom_train.py: phantom train press this switch"),
    ("sc_e_0063", "E0063_00_345_a_01", "lete_river.py: heal party, here we go"),
    ("sc_e_0063", "E0063_00_345_a_03", "lete_river.py: lete river tutorial"),
    ("sc_e_0143_1", "E0065_00_347_a_01", "lete_river.py: lete river what is it?"),
    ("sc_e_0636", "E0636_00_201_a_05", "collapsing_house.py: enter collapsing house dialog"),
    ("sc_e_0636", "E0636_00_201_a_07", "collapsing_house.py: enter collapsing house dialog"),
    ("sc_e_0017_6", "E0018_00_318_a_03", "narshe_moogle_defense.py: There's a whole bunch of 'em + Kupo"),
    ("sc_e_0017_6", "E0018_00_318_a_05", "narshe_moogle_defense.py: Moogles! ... + Kupo!!!"),
    ("sc_e_0881", "E0881_00_486_a_01", "fanatics_tower.py: master kefka's treasure"),
    ("sc_e_0303", "E0303_00_395_a_01", "zozo.py: receive ramuh esper dialog"),
]


def cut_removed_lines(out_root):
    """build()'s last pass: in a WC game each WC_REMOVED_LINES line doesn't show, in every copy
    of its script (the port's own copy where a check already changed it). Returns the files."""
    by_script = {}
    for name, key, _ in WC_REMOVED_LINES:
        by_script.setdefault(name, []).append(key)
    written = []
    for name, keys in by_script.items():
        found = set()
        for f in S.all_paths(name):
            rel_ = os.path.relpath(f, P.VANILLA).replace("\\", "/")
            built = os.path.join(out_root, *rel_.split("/"))
            sc = S.load_path(built if os.path.exists(built) else f)
            ms = sc["Mnemonics"]
            ats = [n for n, m in enumerate(ms) if m["mnemonic"] == "Msg" and m["operands"]["sValues"][0] in keys]
            if not ats:
                continue
            for at in ats:
                found.add(ms[at]["operands"]["sValues"][0])
                skip_at(sc, at, f"wc_removed_line_{at}")
            written.append(S.save_path(sc, out_root, f))
        assert found == set(keys), f"{name}: WC-removed lines not found: {sorted(set(keys) - found)}"
    return written


def build(out_root):
    """Write every changed script under out_root at its vanilla relative path; returns the paths."""
    written = []
    for make in SCRIPTS:
        made = make()
        if isinstance(made, list):          # [(script, vanilla file)]: every copy of a scene
            for sc, f in made:
                written.append(S.save_path(sc, out_root, f))
        else:
            S.save(made, out_root)
            written.append(S.rel(made["Name"]))
    written += [r for r in cut_removed_lines(out_root) if r not in written]
    return written


CHARACTER_IDS = {"Terra": 1, "Locke": 2, "Cyan": 3, "Shadow": 4, "Edgar": 5, "Sabin": 6, "Celes": 7, "Strago": 8,
                 "Relm": 9, "Setzer": 10, "Mog": 11, "Gau": 12, "Gogo": 13, "Umaro": 14}


def track_list(wc_dir):
    """WC's Track menu: every check of WC (its constants/objectives/condition_bits.py) with its
    gate character (constants/gates.py) and our done flag (-1: not in the PR port yet)."""
    import re
    bits = open(os.path.join(wc_dir, "constants", "objectives", "condition_bits.py"), encoding="utf-8").read()
    start = bits.index("check_bit")
    names = re.findall(r'NameBit\("([^"]+)"', bits[start:bits.index("]", start)])
    gates_src = open(os.path.join(wc_dir, "constants", "gates.py"), encoding="utf-8").read()
    gate_of = {}
    for who, body in re.findall(r'"(\w*)"\s*:\s*\[(.*?)\]', gates_src, re.S):
        for n in re.findall(r'"([^"]+)"', body):
            gate_of[n] = CHARACTER_IDS.get(who, 0)
    flag_of = {TRACK_NAMES[k]: CHECK_FLAGS[k] for k in TRACK_NAMES}
    unknown = set(flag_of) - set(names)
    assert not unknown, f"TRACK_NAMES not in WC's checks: {unknown}"
    return [{"name": n, "gate": gate_of.get(n, 0), "flag": flag_of.get(n, -1)} for n in names]


def write_track(path, wc_dir):
    import json
    lst = track_list(wc_dir)
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"checks": lst, "ported": sum(1 for c in lst if c["flag"] >= 0)}, f, indent=1)
    return lst


def write_messages(out_root, message_path):
    """WC's text as Magicite message files (merged by key into the game's): story_mes_en with
    MESSAGES, story_cha_en with an empty speaker for each; returns {file: path} for the keys file."""
    files = {"story_mes_en": MESSAGES, "story_cha_en": {k: "" for k in MESSAGES}}
    out = {}
    for name, lines in files.items():
        path = os.path.join(out_root, "message", *message_path.split("/"), name + ".txt")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="") as f:
            for k in sorted(lines):
                f.write(f"{k}\t{lines[k]}\r\n")
        out[name] = f"{message_path}/{name}"
    return out


if __name__ == "__main__":
    out = os.path.abspath(sys.argv[1])
    for rel in build(out):
        print(rel)
