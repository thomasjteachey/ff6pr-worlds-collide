"""Per-mod settings for the shared SNES -> Pixel Remaster porter (tools/port.py).

Everything in the porter works by diffing a modded FF6 ROM (US v1.0 layout)
against vanilla. A profile only says where the ROM is, where the Magicite mod
goes, and how to read the mod's own text and extras.
"""
import os

import prdata as P

PROJECT = os.path.normpath(os.path.join(P.ROOT, ".."))


class Profile:
    name = "generic"
    mod_name = "Generic"
    rom = None
    rage_odds = (50, 50)  # vanilla: each Rage attack half the time
    rename_all = False    # True: use the mod's names everywhere; False: only names/text the mod changed
    spell_bank_only = False  # True: espers teach nothing in battle (learn rate 0); spells come from
                             # the plugin's SP bank instead (BNW c2.asm "Remove learning spells")
    esper_learning = "snes"  # "snes": an esper's 5 (rate, spell) pairs, the PR's learn rate = the SNES
                             # rate (Ramuh Bolt x10 = 10); "bnw": BNW's 3 (SP cost, spell) pairs
    stat_cap = None  # e.g. 255: raise the PR's 128 caps on Vigor/Stamina/Speed/Magic/magic power
                     # (game_constant_int.csv). Caps are raised anyway where a monster needs it.

    @property
    def out(self):
        return os.path.join(P.ROOT, "mods", self.mod_name)

    def descriptions(self):
        """{"items": [...], "spells": [...], ...} in the mod's ROM, or None to keep the PR's text."""
        import bnw_text
        try:
            secs = bnw_text.sections(self.rom, only=("items", "spells", "lores"))
        except Exception:
            return None
        return secs

    def vanilla_descriptions(self):
        import bnw_text
        return bnw_text.sections(P.VANILLA_ROM,
                                 only=("items", "spells", "lores"))

    def esper_descriptions(self):
        return None

    def extra_data(self, rom):
        """Mod-specific data exported for a plugin (bnw_data.json etc.), or None."""
        return None

    def extra_messages(self, rom):
        """Extra text keys the mod's plugin uses ({key: text}), or None."""
        return None


class BraveNewWorld(Profile):
    name = "bnw"
    mod_name = "BraveNewWorld"
    rom = os.path.join(PROJECT, "build", "brave-new-world", "Brave New World 3.0 FRC.sfc")
    rage_odds = (67, 33)
    rename_all = True
    spell_bank_only = True
    esper_learning = "bnw"
    stat_cap = 255  # c2.asm GengiCheckFunction: stats with gear stop at 255 (EL bonuses at 128)
    defense_flags = "mod"  # every spell's ignore-defense flag from BNW's ROM (port_flags)
    runic_flags = "mod"    # Runic absorbs every black/white/grey spell, no enemy skills (port_flags)
    hit_flags = "mod"      # "can't miss" from BNW (87 spells changed, e.g. Blitzes can miss)
    split_flags = "mod"    # "no split damage" from BNW (52 changed: Quake/Ultima don't split, summons do)
    dance_rules = "bnw"  # Shift/Repeat/Uncommon/Rare at 10/5/1 of 16, no stumble (port_dances)
    # BNW took the MagiTek slots for other spells (c2.asm C21DE0: its Lore/enemy-attack range
    # starts at $84): 131 a monster Fire, 132-137 Rage attacks (Bomb's Exploder, Exocite's Rock,
    # Cephalid's Tentacle, Mantodea's Shrapnel, Blink, Ninja Wave), 138 empty. Mapped onto the
    # PR's Magitek moves (218-225, the Magitek command's ability group 7) they replaced them: the
    # user's new game had no usable Magitek. They get abilities of their own (port_spells
    # add_missing makes the rows); the PR's Magitek moves stay vanilla's.
    spell_relocations = {131: 6131, 132: 6132, 133: 6133, 134: 6134, 135: 6135, 136: 6136, 137: 6137, 138: None}
    # BNW's skill menu shows each dance's four moves (readme "Rage & Dance Moves"): the dance's
    # description lists them (port_dances.describe_steps, as WC's -dda)
    dance_descriptions = True
    event_fights = True        # the story's battles fight BNW's formations (port_events)
    # merchants BNW adds (port_merchants): shop 86 (Dried Meat, Tonic, Tincture, Phoenix Down,
    # Bouncy Ball, Ghost Ring), sold by NPC 16 BNW put in Doma Castle (SNES map 126 at (55, 9),
    # facing left; World of Ruin). The PR's room is Map_20451_2 (laid out 3 tiles lower: the
    # Remedy chest SNES (55, 5) is the PR's (55, 8)), where (55, 12) is walled in: the merchant
    # stands at (55, 10) in the same room, facing left (8; 1 up, 4 down), the PR's merchant sprite
    added_merchants = [{"shop": 86, "group": "map_20451", "map": "Map_20451_2", "x": 55, "y": 10, "direction": 8,
                        "asset_id": 29, "script": "sc_npc_20451_2_bnw_shop", "label": "BNW商人（道具屋）"}]
    # Edgar's tools (port_tools): BNW's Tool_Data_1/2 (c2.asm 2767): tool -> spell it casts
    tool_spells = {0xA4: 0xA4 - 0x27, 0xA5: 0xA5 - 0x27, 0xA7: 0xA7 - 0x0D, 0xA9: 0xA9 - 0x0E}
    mp_heal_spells = [155]  # !ManaBat: restores an ally's MP (no vanilla spell to borrow from)
    tool_rules = "bnw"      # Drill also sets Sap
    slot_jackpot = 151      # 7-7-7 casts Jackpot, 7-7-Bar is a loss (port_slots; reels: plugin SlotRules)
    # single values BNW sets in assembly, not in spell data (port.py ability_overrides):
    # Umaro's Tackle (PR 201 "Slam") is Fight at Battle Power 255 (c2.asm Tackle: LDA #$FF);
    # his ally toss (PR 200) 255 with the Gauntlet bonus, x1.5 = 382 (c2.asm C2/16DA, readme
    # "Umaro Hit Hard"). Odds: plugin UmaroRules.
    # Shock (PR 19, type 618, the Shock command's; BNW's "!Shock", SNES spell 130, which idmap
    # gives to the PR's slot result 214): ignores defense (spell byte 2 bit 5; content flag
    # group 11 = vanilla's group 1 + flag 5); its damage and cost are the plugin's (BattleFormulas)
    ability_overrides = {201: {"standard_value": 255}, 200: {"standard_value": 382},
                         19: {"content_flag_group_id": 11}}
    # sprites (port_sprites): the dragons BNW redrew as Doom Dragon recolors keep their own
    # distinct PR art; Kaiser (new in BNW, vanilla's empty CzarDragon slot) stands in as Doom Dragon
    battle_messages = True  # monster battle lines in compiled AI (battle_text; WC's live seeds don't load story text)
    sprite_keep = {329, 331, 336}
    sprite_standins = {311: 37}
    # encounter types: BNW weights back/pincer/side/normal 33/33/33/156 of 255 (c2.asm:3953;
    # vanilla 31/8/8/208 = the PR's 13/3/3/68+13 preemptive) -> 13/13/13 and normal 48, the
    # PR's preemptive 13 kept; pincers start the party's ATB like a front attack
    game_constants = {**{f"{speed}_{move}_{kind}": (v, 0) for speed in ("LOW", "HIGH") for move in ("DASH", "WALKING")
                         for kind, v in (("NORMAL", 48), ("BACK_ATTACK", 13), ("CROSS_FIRE", 13), ("SIDE_ATTACK", 13),
                                         ("PREEMPTIVE_ATTACK", 13))},
                      "ACCUMULATION_ATB_CROSS_FIRE": (50, 80)}
    # BNW's meanings for weapon special effects that differ from vanilla (byte 27 high
    # nibble; c2.asm Special Effect jump tables): 3 = x2 vs humans + 50% Net, 5 = ignore
    # Defense + x2 vs humans, 6 = 50% Net, B = Windslash, E = 50% Aero instead of the
    # swing, F = undead slayer. Procs (Net, Windslash, Aero) are port_procs' job.
    weapon_specials = {3: {"species": 2, "prog": "ADD_MAGIC"}, 5: {"species": 2}, 6: {"prog": "ADD_MAGIC"},
                       11: {"prog": "ADD_MAGIC"}, 14: {"prog": "ADD_MAGIC"},
                       15: {"species": 1}}  # undead slayer: x2 vs undead (species 1); its 50% kill is plugin todo
    # BNW spell procs from special effects: 3/6 = 50% Net beside the swing, B = Windslash
    # (25%) and E = Aero (50%) instead of the swing (c2.asm 2853, 3061, 2951)
    special_procs = {3: ("add", 0xAD, 50), 6: ("add", 0xAD, 50), 11: ("change", 0x98, 25), 14: ("change", 0x99, 50)}
    # proc odds (bnw 3.0 item.asm RandomCast): 1 in 4; "Use Brush Odds" (byte 11 bit 2) 1 in 2;
    # "High Proc Rate" (byte 12 bit 7, weapon or relic) 1 in 2, or 3 in 4 with brush odds
    proc_odds = {"brush": (11, 2), "high": (12, 7), "brush_rate": 50, "high_rate": 50, "brush_high_rate": 75}
    # status tuning (PR condition.csv rows; BNW engine constants, not ROM data):
    # Haste/Slow x1.2/x0.8 ATB (c2.asm:397; PR rate_value1 = percent, vanilla 50),
    # Image breaks on 1/3 of dodges (c2.asm:1466; vanilla 25%), Sleep and Stop last
    # half as long (c2.asm:3608-3628: 9 vs 18 ticks)
    condition_tuning = {21: {"rate_value1": "20"}, 25: {"rate_value1": "20"}, 19: {"until_value": "33"},
                        7: {"until_value": "15"}, 26: {"until_value": "15"},
                        # Freeze: BNW 10 of vanilla's 34 (c2.asm 3628), the PR's 60 scaled alike
                        32: {"until_value": "18"},
                        # Reflect: no timer in BNW (c2.asm C2/5B06 countdown removed); until_type 2 =
                        # until removed, as Regen. It breaks 85/256 per reflection (plugin)
                        24: {"until_type": "2", "until_value": "0"}}
    # relic bits BNW repurposed: 10.4 Leo's Crest turns Runic into Shock (c3.asm C3/619A)
    relic_bit_funcs = {(10, 4): {(61, 16, 28, 0)}}

    def descriptions(self):
        import bnw_text
        return bnw_text.sections(self.rom)

    def esper_descriptions(self):
        import bnw_text
        return bnw_text.esper_descriptions()

    def extra_data(self, rom):
        import bnw_plugin_data
        import ff6snes
        import port_ai
        data = bnw_plugin_data.plugin_data(rom)
        names = ff6snes.enemies(rom)
        # bosses now running BNW's AI, with their formations, for the plugin's F9 boss test
        data["boss_tests"] = [{"snes_id": t["snes_id"], "name": names[t["snes_id"]].name, "parties": t["parties"],
                               "hybrid": t.get("hybrid", False)}
                              for t in getattr(port_ai.port, "boss_tests", [])]
        return {"bnw_data.json": data}

    def extra_messages(self, rom):
        import bnw_plugin_data
        return bnw_plugin_data.messages(bnw_plugin_data.plugin_data(rom))


class BeyondChaos(Profile):
    name = "beyond-chaos"
    mod_name = "BeyondChaos"

    @property
    def rom(self):
        import glob
        seeds = sorted(glob.glob(os.path.join(PROJECT, "build", "beyond-chaos", "*.sfc")))
        return seeds[-1] if seeds else None


class WorldsCollide(Profile):
    name = "worlds-collide"
    mod_name = "WorldsCollide"

    @property
    def rom(self):
        import glob
        seeds = sorted(glob.glob(os.path.join(PROJECT, "build", "worlds-collide", "*.sfc")))
        return seeds[-1] if seeds else None


PROFILES = {p.name: p for p in (BraveNewWorld(), BeyondChaos(), WorldsCollide())}
