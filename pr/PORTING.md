# SNES FF6 mods → FF6 Pixel Remaster

Goal: make FF6 PR (Steam, build 20265119) play like the SNES mods in this project
— Brave New World first (as close to BNW 3.0 as possible), then Beyond Chaos,
Worlds Collide and T-Edition. Data ports through Magicite; mechanics a mod
rewrote in assembly get re-created in a C# BepInEx plugin.

## Live progress (updated after every batch; newest first)

Working through WC and BNW on my own until both are as close to 100% as possible (user's
request, 2026-11-05). Each batch is ported, tested headless, deployed to the PC (and the Deck
when it's awake), then logged here.

| When | Batch | Result | WC | BNW |
|---|---|---|---|---|
| 2026-11-12 (Ultros) | **WC: Ultros' fights say nothing and play no scene, as WC's (the user's Deck: "i was also getting dialogue during an ultros battle for WC").** The seed's boss shuffle had put Ultros 1 (party 474's copy 20061, battle script 8004 = sc_b_0065) in the Dream Stooges' place; the seed keeps the PR's own AI for Ultros 1-3 (only Ultros/Chupon's is WC's script), and the PR moved Ultros' scenes into the fights' battle scripts, where the plugin's story rule (field scenes only) doesn't reach. WC removes all of Ultros' chatter (data/enemy_script_dialogs.py cleanup_mod) and the scenes. In a WC game (wc_events ultros_battles): the Lete River script's dive (Event00) ends the fight with no "*sploosh!*"/"*glub glub...*" and none of the raft talk it plays with the party's battle animations (Sabin's jump: WC's lete_river.py cuts it); Esper Mountain's (sc_b_0547) Relm never arrives (Event00 joined her mid-fight with SysCall バトル参戦：リルム and the sketch scene: WC blanks that battle event), and his defeat lines don't show. The PR AI's own lines (AI scripts can't read story flags): the plugin skips Ultros 2's "It's me again! / Long time no see! / Did ya miss me?" and "Imp! Buddy! Pal!", Ultros 3's "Thought you wouldn't see me again?..." (WcEvents.WcRemovedBattleLines, before the story rule; a skipped battle line returns as the test runs' Msg does, which battles already take) | offline: test_wc 102 (new: both battle scripts in the script model, WC and vanilla; the plugin's keys are the Ultros AI's lines). Not run headless (the scenes need Ultros' own AI to reach its mode). Installed on the PC and the Deck with the WC distribution session's recipe work (DLL and scripts' checksums match) | ~99% | 81% |
| 2026-11-12 (FC trip, speakerless lines) | **WC: the Floating Continent trip works; speakerless lines WC removes are cut (the user's Deck, seed gaadl68ix7j4: "flying to the floating continent just puts me back on the blackjack deck and i'm able to take the wheel again"; "i'm gettting dialogue on the three stooges during doma dream and other places"; "it says 'make a party of 3' but lets me make a party of 4").** (1) The trip: the deck's air force state (sc_map_20170 帝国空軍戦中: no wheel, the attack's entities, the fights' counter) sat under the story's airship states (ScenarioFlag1 178/194/226, ScenarioFlag2 26), which a WC game never sets, so the trip's deck (sc_e_0560_2: ScenarioFlag2 29, then the map script) was the plain deck with its wheel; the timed fights ran on it and taking the wheel dropped the trip (the Deck's log: one air force fight, then nothing). In a WC game the deck goes straight to the trip's own test (29 set, 30 not). As WC (floating_continent.py airship_battle_mod): 3 of the 6 air force fights (after the first, on to the fifth: sc_e_1056_1 does what sc_e_1058_1 does after the fourth, without "There's something strange up ahead!"), then Ultros/Chupon, Air Force, the landing; no "All right! Let's find a way onto that thing!" / "Let's form a party of three!" (WC enters at CA/581A and removes the 3-member check with its line: any party up to four is WC's), no air force alert, no "Just ahead are Kefka, Gestahl...". A trip given up before the fix (its flag in a save) is cleared when boarding (sc_e_BJinboard_1). (2) Lines: the plugin's rule skips lines a character speaks and keeps speakerless ones as narration, but the PR leaves the speaker off many party and monster lines. The Dream Stooges' (sc_e_0896/0898/0901: WC doma_wor.py "back off", "we're the 3 stooges", "let's rumble") and 40 more (E.WC_REMOVED_LINES, build()'s last pass, every copy of a script): each found in wc_scene_audit's reachable speakerless lines, matched to its SNES dialog and checked inside code WC removes: Wrexsoul's intro, Phoenix Cave's and Kefka's Tower's "All right. We're going in!", "The Warring Triad awaits...", the final Kefka talk's party lines, the Veldt cave's, Umaro's carving, the Ancient Castle's queen, the Flame Eater's, Mt. Zozo's Cyan, Owzer's painting, the Phantom Train's ghosts leaving and switch, the Lete River's, the collapsing house's Sabin, the moogles' "Kupo", the Magimaster's, Ramuh's. Kept as WC keeps them: questions and their answers (the moogle defense's optional three-parties explanation), signs, letters, puzzles, and the lines WC leaves in (Lone Wolf's "That's far enough!", "We'll have to detach the rear cars!", "Please help him!", the burning house's "Huh!?", Thamasa's children, Gau's father's stove, Vector's guard, Kefka's Tower's weights and its three-parties tip). Not done: WC's rewordings (the Floating Continent's "Do you wish to return?", Ebot's Rock's coral question) | offline: test_wc 100 (new: the trip from the wheel through 3 fights, both bosses and the landing with no line; vanilla's lines outside WC; a given-up trip; the Stooges; every removed line cut in each copy and kept outside WC); the audit's reachable removed lines 41 -> 0. Headless (new WcTest plan fc_trip, run_tests.ps1 -Only fc-trip): from the real wheel (party menu stood in for), the flight, the trip's deck under attack in 27 looks with no wheel, 3 air force fights each on its timer, Ultros/Chupon's trigger walked onto, Ultros/Chupon, Air Force, landed on map 431 with ScenarioFlag2 30; no field line shown; 0 failures. (A first try that ran sc_e_0565 from the test instead of walking onto its trigger never got the fight's next script: the harness, not the game.) Bug fixed on the way: the run rewrote the PC's bestiary with no "Save skipped": the Air Force script's 図鑑登録 SysCall writes it through PictureBookSaveManager.Registration, not its hooked Save; WC test runs now also skip Registration, UpdateDefeatCount, AddTotalSubjugationCount and UpdateScenarioFlags (WcSeeds.Patch); the PC's bestiary restored from the Deck's copy (the user's 11:43 play); the rerun left the save folder byte-identical. Event scripts installed on the PC and the Deck (checksums match); the plugin DLL with the guard and the plan on the PC only, the Deck after the WC bundle work in progress elsewhere settles | ~99% | 81% |
| 2026-11-12 (Doma dream) | **WC: Cyan's dream starts as WC's does, with any party once Cyan is recruited (the user's Deck, seed gaadl68ix7j4, Cyan a starting character: "i rested in wor doma and didn't get the doma dream event").** Doma WoR's bedroom (sc_map_20451_4) still had vanilla's rule for the dream's bed: Cyan in the party, exactly 4 members, Mt. Zozo's Cyan back (ScenarioFlag2 109, which a WC game only sets once Mt. Zozo's check is done) and the dream not done. WC's bed (doma_wor.py sleep_mod): the WoR, the check not done, Cyan recruited (the gate). A WC game now uses WC's rule. The bed itself could not take any party: decompiled (pr/tools/ghidra/dream.txt -> tools/decomp/dream) the PR's split, SeparateCorpsAtDreamscape, which moves the first two members left (after MoveReserveCayenne) into two slots for the Dreamscape's rejoins (ScenarioFlag2 181/183; ev_e_0900 bars the way to the Stooges until both), so a party with fewer than 3 besides Cyan would enter the dream empty; MoveReserveCayenne throws if Cyan never joined. WC's SNES bed (bytes at CB/82B1-8482) keeps the party as it is: no "make Cyan leader", vanilla's 3F 02 00 (Cyan out of the party) sits in the Stooges' intro WC branches over, and "remove first 2 characters" becomes "hide the followers". In a WC game the bed (sc_e_0895) now puts slots 1-4 in the beds (the leader in Cyan's), rests, plays the dream's effect, sets both companions found and goes in: Cyan stays in the party (as in WC; before, he was left in the reserve after the dream unless the reward was a character), no split, no companions to find. Vanilla games keep the PR's rules | offline: test_wc + test_wc_romfree 98 OK (new test_doma_dream_takes_the_party_as_it_is: the bed with Cyan out of a 2-member party and Mt. Zozo not done, closed gate, check done, dream done, vanilla's rule kept; the WC bed's beds, no reserve or split, flags 181/183, no barrier). Not run in game: the dream with Cyan in the party (only Doma's bedroom scripts puppet his entity; the waking scene's -3 puppet overlaps a party slot, as vanilla's bed already does), and a seed without -cg where Cyan never joined (the waking scene still puppets his entity). Installed on the PC and the Deck (both scripts' checksums match) | ~99% | |
| 2026-11-12 (Narshe cliffs) | **WC: the Ice Dragon and the WoR snowfield's save point back; the Cultists' Tower's magic-only status goes with the tower, not with MagiMaster (the user's Deck, seed gaadl68ix7j4: "fight the esper on the narshe cliff even without terra", "it was magi master and i was unable to use attack", "i didn't see the ice dragon in narshe").** (1) Tritoch: no gate in WC (event/tritoch.py has no character_gate), so fighting it without Terra is WC's. (2) Magic only: the SNES's is the tower's battle event bit ($3EBB bit 0, its events set it; C2/534E turns Fight into Magic), whatever boss is there; the PR's is a SysCall ("狂信者の塔状態異常付与" = SystemCall.Current.ZealotTowerConditionStart: the TowerOfZealots condition on every player unit, decompiled `tools/decomp/zealot`) in battle script 9024 (sc_b_2024), which the tower's parties carry (MagiMaster 355, White Dragon 482, its encounters). The seed's swapped event battles copied their target's script, so MagiMaster at Tritoch's cliff and the White Dragon at the Ice Dragon's were magic only, and what WC put in the tower wasn't. Now wc_seed.place_script: a tower party fought elsewhere gets the default loop (script 0, sc_b_none), a script-less target in the tower gets 9024; a boss with its own script (Air Force, Chadarnook, Hidon... 22 of them) keeps it and WcBattles gives the status with the game's own SysCall as the battle is first seen with its units (BattleStarted, from WcScaling.Update; a Harmony prefix on External.Battle.WaitToBattleStart threw at patch time and was dropped). Random encounters already took the place's script (port_encounters copies the set's template party). WC's tower menu (Magic kept beside Fight-turned-Magic, its gau_magic_mod) not compared. (3) The snowfield (sc_map_20030, map 19) reaches its WoR block (save point ev_e_0703 after the Falcon, the Ice Dragon ev_e_0779 until ScenarioFlag2 138) only past ScenarioFlag1 136-138, the WoB's Narshe battle, which WC games never set: a WC game in the WoR now jumps there. Other maps keyed on 136-138 (20040 Tritoch's cliffs: fine in the WoR) not audited one by one. Existing seeds: wc_seed_upgrade.py fixes the copies' scripts in place (no regeneration) | offline: test_wc + test_wc_romfree 97 OK (new: test_narshe_snowfield_has_the_ice_dragon_in_the_wor, test_the_cultists_tower_script_goes_with_the_place); plugin builds. The user's seed 20261108-183811-gaadl68ix7j4 upgraded on the PC and the Deck (parties 20027, 20075 -> 0; 20072 -> 9024; backups in BepInEx/wc-seed-backups). Headless (user OK'd; new WcTest `tower=1`, the user's seed and flags, new game, each swapped event battle started as its event does): 367 -> 20027 and 488 -> 20075 (away) none of 3 with the status, 355 -> 20026 (Air Force, script 9013) 3 of 3 by the plugin, 482 -> 20072 (script 9024) 3 of 3 by the game's script, 0 failures; the first run caught the WaitToBattleStart patch failing. Saves byte-identical after both runs. Installed on the PC and the Deck (WorldsCollide.PR.dll identical) | ~99% | |
| 2026-11-12 (turn cycle) | **WC: where a turn's time goes, SNES vs PR (the user: "look into" the ~2.1 s a turn beyond gauge + wait, seen in the status-timers check).** New WcTest `turn_cycle` records a character's gauge, charge (BattleProgressATB.IsChantTimeActDataByUnit) and act (BattleActExection.inStagingBattleActData / GetStatingAct) every frame with the real and the battle's time; `tools/snes_status_ticks.py turn` does the SNES's in its battle-time counter ($3A3E, C2/1136). PR (Agility 40, auto-battle): the pick 0.03 s, the charge 0.78 (the SNES's Fight wait 0.77), the attack 2.1 s of battle time, the restart 0.02, the fill 6.01 (a SNES gauge 6.06): the ~2.1 s is the attack, and the battle's time runs through it. SNES (Speed 33): the wait 26 moves, the attack 37 moves over 86 frames (1.43 s real) with time running, the refill from its end. So both run time through an action; the attacks take about the same real time (PR 1.40 s, SNES 1.43; the SNES's battle clock counts 1.23 s of it, lagging): nothing to change. Found on the way: (1) the FF6 PR never stops time for an action (BattleProgressATBFF6.Update stops only for BattleUIManager.IsWaiting / IsForceWaiting): the notes that it did (WcAtb, these rows, BNW's "nATB is native") were wrong; corrected (BNW's flagged, not re-checked). (2) Auto-battle runs the PR's whole battle at 1.5x (BattleController.UpdateBattle: IsAuto -> Time.timeScale x 1.5): the headless checks on auto measure scaled time; every timer (the PR's and the plugin's) scales alike. | offline: test_wc 92 (+ FF6_SNES_TESTS: time runs through an attack, 37 moves over 86 frames). Headless turn_cycle: 5 whole turns; picks 0.03-0.05 s, restarts 0.02-0.03; charges 0.77-0.78 (the SNES's 0.77); fills 6.00-6.02 (6.06); battle/real time 1.50 over each turn and over each attack (time runs through it); the attacks 1.40-1.48 s real (the SNES's 1.43); 0 failures, saves unchanged. Installed on the PC; the Deck was asleep: its 06:13 build differs only in comments and test code (deploy.sh --wc --deck when it's awake) | ~99% | |
| 2026-11-12 (morning) | **WC: a distributable zip and a faster start (the user: "how do i distribute the WC mod", then "Yes, fix them"; "the startup is incredibly slow", "the command prompt before the game is the thing taking forever").** The zip carried 1,804 of the game's own files: they're made on the player's machine now (tools/pr_bundle.py, a standard-library bundle reader, byte-identical to extract.py on all 2,374 text bundles; tools/wc_gamedata.py; WcGameData.cs runs it in Load, 42 s once, no restart). The packager refuses game files and a plugin that doesn't run its setup; README: BepInEx build, Magicite, the Proton launch option, how to play; Python's license ships. Startup: the opening logos end once the game has loaded (WcBoot.cs, SceneSplash decompiled; removed later that day at the user's word: "it doesn't really do anything"), and the screen's setup and gameplay hooks run in the first frames (Load 6.06 -> 2.96 s). | Headless PC starts (WC test request, saves blocked): console 15.0 -> 10.3 s, title 32.5 -> 19.5 s, 0 failures; first start with no .complete: setup in Load, startup apply ok, 534/534 Magicite files the seed's; setup self-test = the build file by file; a seed from a set-up install = the project's (wc_parity); test_wc + test_wc_romfree 94 OK. `build/dist/WorldsCollide-PR.zip` 12.5 MB. | ~99% | |
| 2026-11-12 (morning, status timers) | **WC: Stop, Sleep, Reflect, Freeze and Condemned last as on the SNES; Jump's air time and the wait between a command and its act are the SNES's; auto-battle defaults to any Fight-like first-slot command (the user: "ensure it works the same with, say, jump/stop/etc", then "match durations and jump air time", then "fix this for all things that might replace Fight" and "investigate and if needed fix" the command waits).** (1) Timers on WcStatusClock (the SNES's status clock, see the row below): the on-set handlers (C2/46B0 table) start Sleep 18 steps (C2/4634), Stop 18 (C2/467D), Reflect 26 (C2/4687), Freeze 34 (C2/4691), counted on the fighter's steps (C2/5B06; Stop's own on stopped steps, C2/5AA3, holding the rest); Condemned (C2/463F, C2/09B4) 81 - (L + random 0..L-1) from the attacker's level L, 20 past 60, its end as the count reaches 1 (C2/5AB8). Statuses at the start or on arrival (equipment, innate) get no timer, as the SNES writes them directly. The PR's (decompiled `tools/decomp/status_timers`, capstone): each status's clock gains BattleUtility.GetConditionDurationTime a frame against condition.csv's until_value (Stop/Reflect 30/60 by time, Sleep 30 or a hit, Freeze 60 or fire), measured Stop 31.3 s; Doom its own countdown from until_value 40, a count every 2 s (Haste 1.33, Slow 4). WcStatusTicks: GetConditionDurationTime 0 for the four (hits still wake, fire still thaws), the clock takes them off (BattleConditionController.InterruptRemoveCondition); the attacker's level caught in AddConditionFunctionSerial.CheckForceMissCondition, then DoomConditionFunction's own countdown set to the SNES's and moved a count per step (its number and death the game's). (2) Jump: the SNES lands at 224/256 of a wait timer gaining half the ATB's add a move (C2/0B7A, C2/1193): 1.75 gauges, measured 729-743 frames at Speed 33 (722 by the rule). The PR's landing is its landing act's chant (ability 22, type 16, ability_wait 30; BattleProgressATBFF6.GetChantingTime = 20 x wait / ((Agility / 2 + 20) x (Haste 2, Slow 0.5) x the battle speed), counted down at frame time x the battle speed): 14.46 s at Speed 43, measured 16.05 (SNES 10.10). WcAtb sets that chant to the SNES's air time; the jump up has none (the SNES's goes up at the input). A first try timing the landing by the gauge was wrong (the PR's landing isn't the gauge) and never shipped. (3) Command waits: the SNES's C2/067B table per command (Fight, Item, skills 16; Magic, Lore, X-Magic, Health, Shock 32; Morph/Revert 0), then the same wait timer; measured Fight/Steal $10 ready 51-57 frames on (52 by the rule), Magic $20 136; monsters act at once ($322C $FE), Berserk/Muddle/Rage turns too. The PR's chant: Attack 1.64 s at Agility 33 (SNES 0.87), spells (ability_wait 5) 2.74 s (SNES 1.73), items none (SNES 0.87). WcAtb gives a character's act the SNES's wait by its command (port.SNES_CMD), none for Berserk/Confusion/Rage/a continuing Dance; monsters and controlled monsters keep the PR's; an unknown command keeps the PR's (logged once). (4) Auto-battle with no history: the PR builds Fight and, for a character without it, Defend. WcAutobattle defaulted only a first-slot Mug; now any Fight-like first command (one ability at the enemy, no submenu): Steal, Mug, Sketch, Gil Toss and Shock (every monster), Possess, Jump; the empty-history target check, the system-type shortcut and the forced substitution (Mug, Jump, Control, Gil Toss, Dualcast dealt directly) widened alike. Left: Control, Leap, Slot, Mimic, Runic, Pray, Trance (the game's default). | offline: Wc.Tests status clock (timers 18/26/34/18 at normal, Haste, Slow; Stop holds them; none from the start; Condemned counts Lv 1 80, Lv 10 62-71, Lv 30 22-51, Lv 61+ 20, its end; a Stop holds it), test_wc WcStatusTickModel 14 (the ROM's on-set table and handlers, the timers' countdown, Condemned's count, Jump's wait and the PR's ability 21/22, the command wait table against C2/067B through SNES_CMD) + with FF6_SNES_TESTS=1 measured on the SNES (Sleep 2250 frames, Reflect 3297, Freeze 4324, Condemned 30: 3702, Jump air 729-743, Fight/Steal/Magic waits): green. Headless `status_timers` (final): Sleep off 37.33 s (18 steps 36.2-39.4), Reflect 54.40 (26: 53.2-56.4), Freeze 71.46 (34: 70.3-73.5), Condemned from a level-60 attacker counted 19, its end 40.1 s on (40.5); a Jump-first character (Fight gone) defaulted to Jump on auto, its air time 10.14 s at Speed 43 (SNES 10.12; the PR's 14.46); Fight's wait 0.87 s at Speed 33: the third's turns 9.84-9.86 s apart with it, 10.63-11.22 with the PR's own chant (1.07 s more, want 0.78), 0 failures. status_ticks green (Stop 38.3 s held every tick), mug_auto green (the generalized default: "first-slot command 11"), atb green; saves unchanged. Installed on the PC and the Deck (WorldsCollide.PR.dll identical); the combined push also carried the WC mod distribution session's startup changes and the Gogo / Narshe sessions' wc_events.py fixes. Seen, not chased: a turn cycle in the PR has ~2.1 s of running time beyond gauge + wait (the SNES measured ~100 frames between a landing and the next full gauge, too) | ~99% | |
| 2026-11-12 (morning) | **WC: the WoR mines 1F's east tunnel walkable again; Umaro's cliff gated on Umaro (the user's Deck, seed gaadl68ix7j4: "i can't go through that mouth", "the jumping off the cliff needs to be gated by umaro").** (1) Narshe's mines 1F (sc_map_30021_1, maps 323/632): ScenarioFlag1 13 (the opening's mine march over) gates the opening's Magitek walls (ev_e_0001: 64 type-19 colliders), the mine door and the Whelk; WC games leave 13 clear so the Whelk check loads there, which kept the walls up: one on the bridge before the east tunnel's mouth (320,384; GotoMap 336,384 to map 634/325), others closing off the save point's side. A WC game now skips that one set (wc_events mines_1f_map); the other maps' Magitek wall sets key on flags WC's start sets (10, 11). Found on the way: the two map groups' exits are trigger objects each SetEntities enables (decompiled MapModel.SetEntities / FieldController.SetEventEntityGroup, `tools/decomp/entities`), so group order isn't a factor. (2) Umaro's cave (WC event/umaro_cave.py add_gating_condition, gate Umaro): the port gated only the reward when talking to Umaro after his battle; now Tritoch's cliff (sc_e_0781) with the gate closed shows "There's an opening in the cliff." and walks the party back, no jump; open, the vanilla question (also after the check is done, as WC). The vanilla route: Narshe's north exit, the outskirts' north exit, the cliffs, Tritoch's cliff; the jump appears only once Tritoch is beaten (sc_map_20040: ev_e_0781 after ScenarioFlag2 139) | offline: test_wc + test_wc_romfree 92 OK (new: test_umaro_cliff_needs_umaro, test_mines_1f_has_no_opening_walls). Not run in game (a walking check needs the kit harness, which the WC install doesn't load). Deploy: with the "WC mod distribution" session's deploy.sh --wc (PC); the Deck with the shared WC push once the game there is closed | ~99% | |
| 2026-11-12 (early morning) | **WC: the Ancient Castle's queen room no longer stays black (the user's Deck: "i can get to the ancient castle, just not its basement"; seed gaadl68ix7j4, its reward there Gogo).** The room's map script (sc_map_30811_7) cues its own 'stone' on the statue, one frame (Stone_00) of the queen's sprite sheet; a seed whose reward there is a character shows the statue as that character (tools/wc_npcs.py, as WC does), whose sheet has no Stone_00, and the field never faded in. In a WC game the vanilla stone is now cued only while the statue shows the queen's art (new plugin call WC:is_asset <entity> <art>); another sprite stands still in its own Default_00, greyed (WC shows the character in the statue's grey palette). New `tools/wc_npc_anim_audit.py` lists every animation a script plays on a re-skinned check NPC that a replacement sheet lacks: the queen's was the only one missing on every sheet; the rest are poses missing on WC's hidden-NPC sprite (91 -> mapobject 96) or the merchant-like 223, mostly in scenes a WC game cuts. Not confirmed in game; next to check if one shows up | offline: test_wc 81 OK (new: the queen room's cue by art, Default_00 on every re-skin sheet); headless (run_tests queen-room, queen-room-esper): the user's seed regenerated with its exact flags, down the library's stairs: black before the fix (overlay alpha 1, statue art 132), fades in after it; the queen's own statue (esper seed) still gets her stone and fades in. Saves untouched. Installed on the PC | ~99% | |
| 2026-11-12 (early) | **WC: Regen, Sap and Poison tick on the SNES's clock (the user: "is hot/dot numbers ticking at correct times (i.e. regen, poison, etc) for WC?", then "fix it and also ensure it works the same with, say, jump/stop/etc").** The PR's (GameAssembly.dll, capstone + Ghidra `tools/decomp/status_timers`): Regen/Sap every 6 s whatever Haste/Slow (BattleUtility.CalcATBProgressCycleForVital type 2 = 6 / the battle speed), Poison 16 s / Haste 8 / Slow 32 (type 1: BattleUtility.ProgressCycle*Coefficient 16/8/32 / the battle speed), first tick at half, the battle speed setting (x0.5-x2) on all, and a jumping unit's Poison and Sap held (JumpConditionFunction.SetDamageStop; Regen went on). The SNES's (vanilla, WC doesn't touch it; ROM read and measured in the windowless harness, new `tools/snes_status_ticks.py`): battle time every 2 frames (C2/111B), a 16-slot round visits each fighter every 32 frames (C2/5A83), its counter gains 64 / Haste 84 / Slow 32 (C2/09D2, Slow first) and makes a status step past 256; the 8-step table (C2/5AEA) flags Regen/Sap at 0 and 4, Poison at 2, sent the step after (C2/5B4F: Poison first; Regen's flag ticks Sap if the fighter has it; one waiting tick at a time); random step index at the start (C2/2457). So Regen/Sap 512 frames (8.52 s), Poison 1024 (17.04 s), Haste x84/64, Slow x1/2, never the battle speed. Measured: steps 128-131 frames, Regen/Sap 515-516, Poison 1031 (lag frames; its damage grows each tick); a jumping character's Regen and Poison land in the air on time; nothing while stopped (18 steps), the step index waits. New `WcStatusClock.cs` (the clock, game-free) + `WcStatusTicks.cs`: the clock runs in Last.Battle.BattleConditionController.Update (once a frame while the battle's time moves) and gates each tick's own counter (Regen progressTime, Sap/Poison progressCount: 0, or past any interval when the clock has a tick), so the game's own tick (amount, number, checks) runs at the SNES's moment; Jump's hold skipped; units outside the SNES's 10 slots keep the PR's timing. Left as the PR has it: the amounts (vanilla's Poison grows per tick: not compared yet). (Corrected 2026-11-12, WcTest turn_cycle: the FF6 PR does NOT stop time while an action plays; see the turn-cycle row), Stop/Sleep/Freeze/Reflect/Doom durations and Jump's air time (the SNES's: Stop and Sleep 18 steps, Reflect 26, Freeze 34, Doom 20-81 by the caster's level; Jump lands at 224/256 of a wait timer filling at half the ATB rate, 1.75 gauges; the PR's: condition.csv until_value + BattleUtility.GetConditionDurationTime, not measured). | offline: Wc.Tests status clock (Regen/Sap/Poison gaps, Sap over Regen, Haste 12-13 visits, Slow, Slow over Haste, Stop holds and keeps the phase, a held tick doesn't pile up, the first tick within an interval and a step; the 2 `-wmhc` preset failures predate this), test_wc WcStatusTickModel (7: the plugin's constants against the ROM's code bytes, intervals; with FF6_SNES_TESTS=1 the SNES measured: cadence, Poison landing mid-Jump, nothing while stopped): green. Headless `check=status_ticks` (party 7, monsters stopped): Regen 8.522 8.527 8.504 8.529 s, Sap 8.511-8.523, Poison 17.031 (SNES 8.519 / 17.039), Haste's Regen 6.380-6.916 (12-13 visits), Poison ticking on a unit with the Jump status (420), no Regen in a 31.3 s Stop (the PR's Stop; the SNES's is 18 steps, 38.3 s) and on again after: 0 failures; saves unchanged. Installed on the PC and the Deck (WorldsCollide.PR.dll identical; this push also carried the other session's Ancient Castle queen-room fix) | ~99% | |
| 2026-11-12 | **WC: the checks' own scenes trimmed to fight and reward (the user's Deck: "there's still a cutscene with edgar in disguise").** Figaro Castle WoR's engine room played Gerad's thieves' plan, Edgar's reveal and join (with an auto-equip) before the Tentacles, then ~440 instructions of thieves and Edgar before the reward: now Gerad -> the Tentacles -> the reward, the done flag, the room reloaded in its after state (the scene had hidden the party for good). The sweep had judged check scenes by their hooks; new `wc_scene_audit.py --checks` runs every check script and the scripts its fight or map change leads to in the offline tests' script machine as a WC game takes it (gate met, not done) and lists the puppet routines and story lines left. Trimmed the same way: the opera after Ultros (reward, deck), the collapsing house after the escape (countdown stopped, reward), the Sealed Gate's opening (reward on black, cave), the Doma siege's victory (reward, out), the burning house (straight in without Strago's plea and join; after the Flame Eater the reward, then Strago's house with its flags, no wake-up scene), Baren Falls' riverside (no Gau scene), Ebot's Rock (the reward instead of Strago's cheering, then straight to the end of vanilla's Thamasa chain: no Gungho, dinner story or Relm scenes). Left by --checks: short routines around fights (Vargas, Ultros' statue, Tritoch, Narshe's battle end), the auction's bidding, the moogle defense's start | offline: 85 WC tests (test_wc 73); deployed (deploy.sh --wc, PC + Deck) | ~99% | |
| 2026-11-12 (late night) | **WC: monsters use and throw items, steal, jump and toss gil the WC way; 11 more on WC's scripts (319 of 384).** (The user: "Yes, continue".) WC's F6 (use / throw item) and F4 (command) as the PR's own monster abilities, the way Square ported them (tools/port_ai.py item_abilities, the seed's snes_ai item_use / throws / commands, WcEnemyAiHooks.Plan): a thrown weapon is a hit with its power (Square's 740-756 match WC's weapons by power: Dagger 26 = 741 ... Fuma Shuriken 132 = 747; our own rows 7700-7703 for Ichigeki, Kazekiri, Murasame and Pinwheel, with the knife/katana/shuriken animation), a used item is an ability with the item's own effect (Square's 760 is Hi-Potion; 7704-7710 Potion, Ether, Hi-Ether, Elixir, Gold Needle, Remedy, Green Cherry), Steal 693, Jump 1391, GP Rain 694 (GIL_THROW); Capture has no monster version: Steal. Now on WC's scripts: Mugbear (steals, then runs), Leap Frog (jumps), Harvester, Magic Urn (uses its potions and ethers on you, then flees), Covert, Blade Dancer, Outsider (counter-throws shuriken at its attacker), Punisher, Amduscias, Guardian, Dadaluma (heals itself with Hi-Potions, brings its Iron Fists, throws knives, jumps, steals back). SNES_AI_VERSION 3 (tools/wc_seed_upgrade.py allows rows 7700-7799). | offline: test_wc 72 OK; headless: the 11 in kept-alive runs (every throw, item use, steal and jump seen and mapped; no stall), Dadaluma in a natural fight (all of the above, its death erases everyone, won). Installed on the PC and the Deck; the user's seed upgraded on both (backups in BepInEx/wc-seed-backups) | ~99% | 81% |
| 2026-11-12 (night) | **WC: monsters come and go the WC way (AI command F5), 7 more monsters on WC's scripts (308 of 384).** (The user: "oh yeah continue doing that i reckon".) F5 brings monsters in and out mid-fight: Satellite's alarm calls Imperial Soldiers, Flame Eater brings its four balloons (and back when they blow up), Ultros calls Chupon in at low HP, the Angler Whelk's head hides, the Tonberries' step, Goetia's helpers; on top of WcEnemyAiEntries.cs (InBattleEnemy into the member's cell, the PR's Escape and the sprite off for a hide): (1) arrivals queued, one at a time (InBattleEnemy keeps one pending load: four balloons came as one); (2) a death's entries wait for its death animation and belong to their battle (a Trapper of Chaser's turned up in the next fight); (3) a death that brings others in holds the battle open (it ended before Chaser's Trappers came), every hold released within 15-30 s; (4) a monster shown comes back from the PR's own hide conditions too (Chadarnook's NoSelectChad), and a hidden monster gets no turn or counter (an attack already on its way hit the painting, whose counter brought it back). Kept on their earlier AI: F5 users whose PR battle routine decides who is on screen (ENTRANCE_SCRIPTS: Chadarnook's form switch ended that fight at the swap; Hidon, Air Force's bays, the blades, Soul Saver...) or brings monsters in itself (BattleAddEnemy: the Flans' split, Chaser's Trappers, Opinicus). tools/port_ai.py SNES_AI_F5 (on) and SNES_AI_VERSION 2; tools/wc_seed_upgrade.py brings a version-1 seed up (the user's: 7 AI scripts, 2 tables). Self-test enemy_ai_each@N+N... (fights in one run, monsters kept alive, Nk / No / Nl to kill it, its partners, or hold it at low HP); its per-frame HP refill could leave a monster at full HP with the KO condition (a balloon's self-destruct), which stalled two forced ends: test-only. | headless: the 9 fights in kept-alive runs (Flame Eater's 4 balloons, Satellite's soldiers, Chaser's 3 Trappers with the battle held open, the Flan's split, Chupon, the Whelk's head, the Tonberries' step); then natural fights, one per launch, won by the party with no HP tampering: Flame Eater (balloons in and back, its death erases everyone), Satellite (soldiers, "An alarm sounds!"), Goetia, the Angler Whelk (head hides, death erases everyone), Tonberries (all three step; the party kept missing). Shipped with the other sessions' WC fixes on disk (the esper Magic command, the auto-battle Mug watchdog, the story-scene cuts). Installed on the PC and the Deck; the user's seed upgraded on both (backups in BepInEx/wc-seed-backups) | ~99% | 81% |
| 2026-11-12 | **WC: the check dungeons' story scenes cut (the user: "do dungeon scenes first"; "figaro cave also has some cutscenes that are skipped in wc snes").** Each against WC's own event module, all guarded by WC_GAME. Phantom Train (`phantom_train_scenes`): no finding/boarding/departure scenes (the rear door's flag set at boarding, so the front door works at once), Sabin's lever does nothing, the friendly ghosts don't join, after the roof fight the ghosts already stand round the party (the map's own positions), the ladder and the plan to detach skip to their flags, the lever detaches without going outside, the dining car only heals, Siegfried just fights. Doma's dream (`doma_dream`): no Elayne/Owain on arrival (its save point and flags kept), no family flashbacks, no farewell after Wrexsoul (straight to Cyan waking, trimmed to the reward), no Dream Stooges' intro after resting. Towers (`tower_scenes`): the Floating Continent's statues go straight to the escape's script (sc_e_0594_2: flag, countdown); Kefka's Tower's first landing has no flight round the tower and no deck talk. WoR (`wor_scenes`): Figaro's cave turtle already shuttles (no thieves' ride), Siegfried doesn't run off, Mobliz has no Katarin scene and no Terra/children scene after Phunbaba (end positions, reward), Gau's father has no repairman scene; Figaro's twins flashback, Locke's Phoenix Cave reunion cut. Kept as WC keeps them: battles, traversal (Vector's crate, Lete River's raft, Baren Falls' dive, Sabin's roof jump), the Sealed Gate's opening, the Veldt cave's Interceptor, Solitary Island's cliff, Duncan, the collapsing house, prompts (rest, leave, airship). Vector's palace stays behind its Guardian (behind it the banquet would still start) | offline: 72 WC tests (5 new); the audit's fresh walk-in list 78 -> 64 triggers (the rest: battles, prompts, traversal, check flows); deployed by the WC enemy-AI session's deploy.sh --wc | ~99% | |
| 2026-11-12 | **WC: story scenes outside the checks cut (the user's playtest: Setzer and Cid's engine-room scene on the Blackjack).** The plugin skips the story's lines in a WC game, which left vanilla scenes playing as silent animations. New `tools/wc_scene_audit.py` follows every map script, placed entity and the scenes they start under a WC game's flags (fresh start, or a fixpoint of every flag a reachable script sets) and lists what can still show lines, fight, play puppets or run a vanilla join (`--joins`). Cut in a WC game (`story_scenes`, all guarded by WC_GAME, so existing saves get them): the Blackjack's engine-room stairs (Gestahl's dinner over, the scene's flag never set), Narshe WoR's thief at the gate, Doma Castle's poisoning scenes (WC marks them seen), Esper Mountain's espers and Yura, Kefka throwing Ifrit/Shiva away, Kefka's jail (his monologue started the dinner's timer), the hideout's sleep-talking soldier, Gerad overheard in Figaro's prison, Baren Falls' talk (Shadow left), Sabin's cabin, Kohlingen's old man and Rachel's flashback (WC deletes it), Shadow in South Figaro's pub (his naming screen; WC hides him). Free joins found by the audit: Setzer in Kohlingen's WoR inn (WC never places him) is gone; Darill's tomb's stairs now give the reward and lead out to the WoR (two Setzer flashbacks and the Falcon hangar, which has no way out, skipped). Magitek Factory's end as WC's: the tubes give the third reward with no Kefka/Celes scene (it made Celes leave the party), the elevator goes straight to Cid, Cid starts the mine cart without the escape flag (194 brought Setzer's meet-up, his join, the Blackjack and Zozo scenes back to Vector), the cart ends in the Cranes fight, then the party is outside Vector on the Blackjack. Bug fixed on the way: `gate_at` closed a module's later rewards once its first was taken (Number 024, the tubes, the Lone Wolf's hairpin). Left (silent scenes inside check dungeons WC also strips): the Phantom Train's (find/board/door/lever/detach/ghosts; its friendly ghosts still join), Doma's dream family scenes, Kefka's Tower's entrance split, the Floating Continent's statues, Mobliz before Phunbaba, Ebot's Rock's chest intro, Figaro cave's turtle, Gau's father in the WoR | offline: 67 WC tests (7 new: the cut layers place in vanilla and not in WC, the stairs, Darill's tomb, the factory end, the later-reward gates); the audit's fresh touch/entry list 91 -> 78 (the rest: checks, prompts, battles, rests). Not deployed yet: the game was running on the PC, the Deck asleep | ~99% | |
| 2026-11-12 | **WC: auto-battle Mug stalls (the user, Deck seed gaadl68ix7j4: "he just sits there with a full ATB bar (sometimes it works but not all the time)"). Cause not found yet; two replay faults fixed, a stall watch added.** Decompiled the replay step the 11-11 patch skipped (ActSelectAuto.CreateActDataAuto(BattleActData); Il2CppDecompile.java kept only the first overload of a name and now writes each one, `Name#rva`). That step is where the game picks a replay's targets (ActUtility.GetDefaultTarget) and builds a fresh act (IsAutoBattleAct). The patch reused the remembered act as it was, so: (1) a new battle's first replay (the last battle's act, kept in CommandHistoryData; IsPrevBattle clones it, PrevTargetChange marks it for a default target that only that step applies) went at a monster of the previous fight; (2) a replay whose monster had died went at the dead monster (headless: a wasted turn, an act with no command). WcAutobattle now rebuilds the Mug the game's way: the remembered target while it is a monster of this fight that can be targeted (alive, not petrified, BattleUtility.IsNotTargeting false), else a random one (RandomEnemyTarget). The 11-11 bypasses of IsNotTargetALL and the SystemId 3 shortcut turned out to be "every monster down or hidden" checks, where the game makes the unit wait: they now apply only while some monster can be mugged. Neither fault stalled a character headless, so the user's stall is still open: a watch (WcAutobattle.Watch, from the runner's Update) logs once per battle any party member on auto that sits 4 s with a full gauge and nothing queued, with whether the game still asks it for an act, its conditions, its last auto act (targets in the fight or not) and the last auto-battle path taken. Also decompiled: the executor drops an act whose targets fail its target conditions without resetting the gauge (BattleActExection.IfNeededRemoveStackNotAct). The user's Gau has [26 11 18 3] (the hidden Magitek slot, Mug, Sketch, Magic); WC's -sch steal override reads the monster's master steal slots, which never empty (repeat steals succeed) | Plugin builds; Wc.Tests: its 2 flag failures ("unknown tokens -wmhc") predate this. Headless mug_auto (new: two battles; battle 1 is party 9 with all but one monster dying; the in-battle unit's command list made Mug-first, as a seed's is, which the field-side change hadn't reached so every earlier replay was turned down): fixed replay green (Mugs at monsters, a dead target re-aimed, a Mug at the first battle's monster re-aimed, mugging on in battle 2); with mug=old (the earlier replay) also green, so neither fault is the stall. Saves byte-identical after the runs. Deploy: pending (another session's generator change on disk) | ~99% | 81% |
| 2026-11-12 | **WC: Thamasa's shops (the user, Deck seed gaadl68ix7j4: "shops in thamasa aren't talking to me").** The PR picks the shopkeepers of Thamasa's three shop rooms (sc_map_20271_1/2/3) by ScenarioFlag2 12, set only by vanilla's landing near Thamasa with Leo (sc_e_0518_1, world map): before it the rooms hold stand-ins with no script (ev_e_0001), after it the shops (ev_e_0518, groups 48-51, which the seed randomizes). WC never plays the landing, and the flag can't be a start flag (it also arms the town's arrival scenes, ev_e_0519/0520). wc_events.thamasa_shops: in a WC game the rooms always take the shops (both worlds use them). Same flag in Strago's house (sc_map_20271_5): a script-less Strago stand-in stood there for good, silent even with Strago in the party; WC's start clears his at-home NPC bit, so in a WC game nobody is home (wc_events.thamasa_strago_house; vanilla's house after the fire has no entity set either) | offline: 58 WC script tests (new: the shop rooms in the WoB and WoR, vanilla unchanged, no arrival scene armed; Strago's house empty in both worlds). Not played in game. Installed as Magicite files only (the four scripts + map_20271's key file) on the PC and the Deck, checksums match; not via deploy.sh, as another session had unfinished WC plugin changes (enemy AI) on disk | ~99% | 81% |
| 2026-11-12 | **WC: monsters run WC's own AI scripts, read by the plugin the way the SNES reads them.** (1) Atma at Doma (the user: "ultima cast something that oneshot one of my party members"): reproduced on WC's SNES in the windowless harness: the one-shot is WC's (Atma's counter counts hits in a shared variable; the 13th arms "Unknown light surrounded Atma!", two idle turns, then its big spell); the port had no charge-up. (2) `WcPlugin/SnesAi.cs`: FF6's AI interpreter (everything8215's ai_cmd.asm: turns restart at the top, failed checks skip to the next FE, loops at FD, the counter section with its own state, targets, conditions 00-1C, variables and switches, timers); `WcEnemyAi*.cs` run it from the PR's own instructions: a monster's PR script is a shim (tools/port_ai.py shim_script) whose marker rows hand its turn, counter and death to the interpreter, and the PR queues the acts (its waits, targets, animations). The user, mid-way: "please make all enemies/bosses behave like WC SNES": 301 of 384 monsters now do (and the PR's own copy of Holy Dragon in its fights); still on their earlier AI: 39 bosses whose PR battle events read the PR's AI (modes, variables, scripted entrances) and 44 whose WC scripts bring monsters in or out (F5), use items or commands, or run battle events. (3) WC's slots: the seed maps each party's cells to the SNES formation's slots (every party a formation's battles use, the event battles' own), so scripts that aim at or check slot $30-$35 get the right monster (they went by cell order); the battle's party is taken where the battle is made (the lookup read 0). (4) Battle lines: WC's monster lines show as the PR's own text (its keys, so in the player's language): 47 matched by monster and wording, 3 short ones of our own; player-made seeds had none (the shipped generator carries no SNES dialogue, by design). They reach the window through the PR's ExecMsg (the battle flags the acting monster; ViewMes reads the lines filed under the staged ability). (5) Found for the F5 work: an SNES formation's byte 1 is its shown-at-start mask; 13 vanilla formations have members outside it (Air Force's Bit, Chupon, Chadarnook's demon, the Flans, Flame Eater's balloons, Dadaluma's Iron Fists, Ifrit/Shiva...), which the porter has been leaving out; the PR brings such monsters in with AddEnemy abilities (701-775, 1249). (6) `WcEnemyAiEntries.cs`: F5 the PR's way (a member comes in with EventCommand.InBattleEnemy into its cell, as Square's AddEnemy abilities do; one that leaves gets the PR's Escape condition, which the probe showed stops its turns and targeting, and its sprite is switched off; the SNES's battle end when nobody's left is the PR's erase-all); for now only the death blocks of monsters already on WC scripts reach it: the F5 users stay on their earlier AI until their fights pass (tools/port_ai.py SNES_AI_F5: with it on, 9 more monsters run WC's scripts: Satellite, Flan, Chaser, Goetia, Tonberries, Flame Eater, Angler Whelk, Master Tonberry, Ultros with Chupon; a first run found Chadarnook's PR battle routine re-hiding its demon by NoSelectChad and ending the fight at the swap, so a monster whose F5 meets a PR routine that decides who is on screen (ENTRANCE_SCRIPTS) keeps its earlier AI; showing a monster lifts the PR's own hide conditions; a hidden monster gets no turn or counter; a death's entries wait for its death animation). (7) `tools/wc_seed_upgrade.py` also brings a seed to the plugin's AI (the user's seed: 301 AI scripts, the monster and AI-script tables, 3 marker abilities; anything else differing stops it). | offline: the interpreter on every monster's script, 165,649 checks; headless: Atma (16 turns, 42 counters, its charge line shown, its death erases everyone like a boss, won), Cactrot (its two lines, the 10x turn), Intangir (asleep until hit, then two acts a turn), a 40-fight sweep (every fight started and its monsters took WC turns; one battle ran 17 minutes of WC turns without a stall: the test's forced end didn't apply to its battle type, fixed), after the fix 40 more fights (all started, ran WC turns and ended) and, on the final build, Atma again and 10 more fights; SNES harness: formations 123, 433, 459, 464 at the start of battle. Installed on the PC and the Deck; the user's seed upgraded on both (backups in BepInEx/wc-seed-backups). 01:20 build also carries another session's esper fix (WcEspers.RestoreMagicCommands: the Magic command is kept, as on WC's SNES; its feature checks passed headless), on the PC and the Deck | ~99% | 81% |
| 2026-11-11 (night) | **Modding kit for any Pixel Remaster (the user: "add modding infrastructure that will help mod this, ffv, and other ff pixel remasters so that future features are easy").** `tools/kit.py` + `tools/prkit` (the six PRs found through Steam; extract, api dump, BepInEx setup for another PR, new-mod scaffold, Magicite writer and checker, manifest-driven package/deploy for PC + Deck, headless runs), `pr/modkit.json` (bnw, wc, hd2d), `plugin/PrKit` (Hooks, Feature/FeatureHost, Master/GameText, TestRun, Harness, LiveData, MagiciteDir, SingleGamepad; `PrKit.props`), `pr/MODDING.md`. Then (the user: "Yes, move them") BNW's plugin onto the kit (v0.22.1: PrKit.props, 236 hook calls on `Hooks.*`, the kit's harness and save block); WC's move is scripted and agreed with the WC session, held until it frees WcPlugin. `package_mod.py` reads the manifest. No mod percentages change: tooling only | offline: test_prkit 20 + its dotnet build of a scaffolded mod, PrKit.Tests 5/5, BNW builds with the kit inside, Rules.Tests 31/31, BNW/WC Magicite folders validate with 0 problems, BNW and WC zips identical to the old packager's file by file; the kit not yet run in game. Nothing deployed | ~99% | 81% |
| 2026-11-11 (evening) | **WC: field walking stutter, Doma's siege entrance, the Ultima fights' lines; a windowless SNES harness; Doma's siege side by side with WC on the SNES.** (1) Walking stutter (the user: "why is the fps choppy?" ... "only when walking on the field"): WcEncounters found the FieldController with FindObjectOfType on every step (22.6 ms); now cached (0.005 ms). (2) Doma's siege (the user's Deck: "doma siege ports me halfway up the map. it also flashes at the very beginning"): the world map exit makes vanilla's own map change into the siege (WcEntrances: EventGotoMap to map 75 point 2 with sc_e_0099_3 reserved for the arrival, the siege's music kept): the castle's entrance no longer shows first. (3) The Ultima fights' opening lines (the user: "ultima buster shouldn't have in battle introductory text"): sc_b_0592 17-20 and sc_b_0977 19-25 jumped over in a WC game (wc_events ultima_intros), as on WC's SNES. (4) tools/snes_harness.py: the SNES in-process (RetroArch's Snes9x libretro core through ctypes; its bsnes cores give no work RAM): no window, no input devices (Mesen 2's test runner took input from the user's other game), work RAM read and written, PNG screenshots, below-normal priority, 1,000-1,450 frames a second. (5) Doma's siege, WC on the SNES (the user's seed and flags, entered from the world map tile) vs the port (its scripts and headless run). The same: the gate scene, control at (33,49), the captain at (33,54) and his battle at once (Atma: HP 4,920, MP 1,698, EXP 4,515 a character, 0 gil; level 6 + random -1..+2 on both: SNES 5 and 7, port 8), no lines before or after it (the port's story rule hides the four WC removes), the fleeing soldiers, 8 soldiers to fight (3 more behind the south push-back row on both), the esper (Shiva), no opening lines in the Atma battle. Different: WC's two push-back lines ("Thou musn't give up the fight!" toward the castle; "DOMA SENTRY: Sir! Let their commander have it!" toward the south, also the two sentries' line) are silent in the port; after the reward WC loads Doma at (33,53) facing the steps, the port at its south entrance (33,61); arrival to control 10.5 s on the SNES (3.9 s of it black: the camera's pan to the gate) vs 8.7 s headless (2.9 s black); the SNES shows the gate for one frame at 1.2 s. Not measured on the port: after the battle (its run was stopped) | offline: test_wc (Ultima lines skipped); headless (port): the Doma entrance, the walking hook's cost; SNES harness: the siege from the world map tile to Doma's map in 21 s. Installed on the PC and the Deck (deploy.sh --wc --deck after it woke: the plugin's checksum matches) | ~99% | 81% |
| 2026-11-11 (later) | **WC parity: Doma's siege, check-scene text, ability scaling at cast time, the SNES's ATB and random battles.** (1) Doma (the user, Deck: "when i enter doma castle siege, its just freeze framed"): the WC start of the siege started its scene script from inside the map script (ChangeScript); it now enters the way vanilla does (ChangeMap to sc_e_0099_3, flag 72), wc_events doma_siege_start; headless doma_start green. (2) The user: "are there any other events that either have text and shouldn't or aren't on parity with WC SNES": every check module's scenes run in the script model with the plugin's story-skip rule (now test_check_scenes_show_no_story_text): only the auction house's and Tzen's sale talk show lines (as in WC); Ebot's Rock's "That evening..." narration (sc_e_0950_3, no speaker) is jumped over in a WC game (ebots_rock_evening). (3) Ability scaling (-ase / -asr; the user: a Veldt Sandhorse's Wind Slash one-shot Gau): WC picks each scaled spell when the monster acts (tier = min(level x 2 // int((factor + 3) x 2), 8) + the placeholder's offset + a random -2..+2 weighted 25/64/89/64/14 of 256, then any spell of the tier); the port had picked one spell per script from the monster's unscaled ROM level (Sandhorse, level 27: always Wind Slash; WC at its scaled level 7: Gale Cut 70%, Wind Slash 15%, Sand Storm 15%). Now placeholder k compiles to ability 7600 + k (a row copying a spell of its category, ai_compile SCALED_BASE), the seed carries WC's tiers as PR abilities (wc_seed.json ability_scaling) and WcAbilityScaling swaps the spell in ActSelectEnemy.CreateActData, before the game picks targets (the AI's Act passes none; a counter's target follows CounterAct's own rule, BattleTargetSelectUtility.IsSelf). Damage itself was already WC's (Wind Slash power 48, no split on the SNES either, monster magic x1.5 as vanilla). Seeds made before: tools/wc_seed_upgrade.py regenerates one with its seed and flags, refuses unless only the AI scripts, the new rows and the table differ, backs it up (BepInEx/wc-seed-backups) and brings them in; the user's seed upgraded on the PC and the Deck (152 files, copies identical). Left: WC's special-attack tiers (0x5E-0x60: the monster's own special), bosses that keep the PR's script. (4) ATB (the user: "is ATB/etc on parity with WC too?"), measured headless (WcTest atb): the PR's party filled 0.260 x (Speed + 13) gauge-% a second, monsters 0.216 x (Speed + 21), Haste x1.5, and its battle speed (5 steps, x0.5-x2, BattleUtility.GetBattleSpeedByConfigValue) on both sides; the SNES: 0.275 x (Speed + 20) for both, Haste x1.3125, Slow x0.5, Bat.Speed on monsters only (x(255 - 24 n)/256). So the party came 11-37% slower than in WC while monsters matched. WcAtb answers BattleProgressATBFF6.ATBCalc with the SNES's rate (the PR's 5 battle speeds = the SNES's Bat.Speed 6, 5, 3 (default), 2, 1 for monsters). Left as the PR has it: time stops while an action plays (the SNES's gauges and status timers run on), the gauges' start (PR 50-80% at random, SNES Speed-based). (5) Random battles (the user: "can you ensure the encounter rate is on parity with the WC snes"): the PR draws a threshold of 26-44 steps and past it finds a party 25.6% of the time a step (1-1000 against the sets' 80/80/80/16 = 256), ~38 steps everywhere; the SNES's danger counter (kept in the save) gains a map's or world zone's rate each step and rolls its high byte: mean 23 steps on world maps, 31 elsewhere; 7 maps and 7 World of Balance zones are low or high (zone 10's forest normal), Charm Bangle half. WcEncounters hooks the step's one call of CheckEncount (FootMonitoring.ImmediatelyBeforeMoveingAction: CheckEncount itself can't be patched or called from managed code, its "in List<OwnedCharacterData>" parameter made Harmony's wrapper an invalid program, which froze the party after 4 steps in the first try, PC only); the PR's counter holds the SNES's danger with its threshold out of reach, each step is rolled the SNES's way before the PR's check, and a battle puts the counter under the threshold and draws the party 1-256 (the SNES's 5/16 x3, 1/16), so the PR's own check, party pick, Veldt fights, background, attack type and preload run as they do; its re-roll after a battle (Shuffle) keeps the danger but after a random battle; the world zone is the one at the world map's last load, as on the SNES. | offline: test_wc 55 (new: the ability tier formula and its in-game grid, placeholders in a real WC port, the ATB numbers, the encounter tables from the ROM and the steps-per-battle means). Headless: ability_scaling (400 acts at the Sandhorse's level: WC's odds, all at the party; its own turns swapped), atb (party, monsters at each battle speed, Haste, Slow = the SNES's to 0.2%; real fill within 0.5%), encounters (zone 10's low rate south of Narshe; the hook's counter 32.5 steps per battle at it, SNES 33.6; a walking step through the hook and the PR's own check setting the battle up; the battle's start itself can't run headless: the PR's own random battles stop a -nographics game there too), doma_start; regression (doma_start, veldt_reward, kt_skip with the new hooks): 0 failures. Installed on the PC and the Deck (the Deck after waking: plugin, generator and the upgraded seed's files match the PC's) | ~99% | 81% |
| 2026-11-11 | **WC playtest fixes (Deck seed gaadl68ix7j4): the Veldt reward, auto-battle Mug; BNW set aside (the user's call).** Not logged here: WC work between 2026-11-08 and 11-10, made outside this log: auto-battle replay of the PR's accessory commands WC assigns directly (WcAutobattle.cs: Steal/Mug, Jump, Control, Gil Toss, Dualcast), the Veldt reward encounter and its stand-in actor (WcVeldt.cs), the Veldt check skipping Gau's tutorial and recruitment scene (wc_events veldt/veldt_battle), Ghidra digs into auto-battle, the Veldt and transport. (1) The Veldt (the user: "fading to black twice with the vanilla gau there", "ported to serpent cavern entrance for no reason"): the dried meat has two branches in the Veldt's battle script (sc_b_0239): event 00 rejoins Gau (SysCall "バトル加入：ガウ", Current.JoinGauInBattle, as after a Leap) and event 01 is his first join (SysCall "ガウ初回予約", Current.ReserveScriptGauFirstJoin), which reserves sc_e_0239 on the world map: it parks the Blackjack at (1, 1), boards and leaves it, moves the party to the scene map 574 (sc_e_0239_b: vanilla Gau and party staged there, the reward line) and ends at the Crescent Mountain cave (world map point 16). A WC game's Gau never sets ScenarioFlag1 126, so its reward encounter takes event 01; the in-battle reward of 11-10 only hooked event 00 (the Deck's log: the field path ran, "check veldt: esper 77"). Now the plugin turns the first-join reservation away in a WC game: with Gau in the party and the check open, WC's reward right there in the battle (WC event/veldt.py), no scene, the party where it fought; a Gau out of the party (back from a Leap) gets the game's own rejoin. The 11-10 rejoin hook had also swallowed every Gau rejoin in a WC game (a Leap would have lost him): it only takes the reward encounter now. sc_e_0239 itself, if it still runs in a WC game: the reward on the spot (check open) or nothing, never the parking, the boarding or another map. (2) Auto-battle Mug (the user's Gau, Mug as his first command, "targeting myself"): with no history the game builds Fight and, for a character without the Fight command, swaps it for Defend on the character itself (ActSelectAuto.CheckAndReplaceFightActData: ability 2, target the unit, TargetType Self); WcAutobattle's first-slot Mug copied that act's target, and replays keep a Self target (PrevTargetChange retargets single-ally/enemy acts, not Self). The default Mug is built from the game's own Fight (a random enemy) and only when it is the plain Attack (Magitek armor's beam and the Cultists' Tower's Defend stay); a replayed Mug aimed at its own side gets a random enemy (BattleUtility.RandomEnemyTarget). (3) WC test runs now block the bestiary and the config like the BNW probe (the test battles had rewritten the PC's bestiary: restored from the Deck's, identical bytes). (4) The Veldt reward's line (the user: "why is 'obtained X' done through a custom menu instead of what already exists inside ff6") was the plugin's overlay box (WcScreen.Toast, added with the 11-10 in-battle reward); it now goes in the battle's own notice window: the battle script shows WC_REWARD with Msg [2, 0] (the window of "Dispelled the curse on the shield!") right after the dried meat's SysCall when the plugin flags a reward (ScenarioFlag4 129), before the battle ends; no fanfare, as the game's battle notices have none. Every other check already used the game's message window; the overlay box is left for WC's own notices (an objective done, an esper a character can't equip, a seed that failed to load) | offline: test_wc 47 (new: the world map's Gau script in a WC game: no parking, no map change; the reward line in the battle window, in both dried-meat branches, and nothing without a reward). Headless (new WcTest plans): veldt_reward (the game's own first-join reservation: the reward in the battle, nothing reserved, no party pick pending, its line waiting for the battle window without the field's centring tag), mug_auto (Mug first and no Fight on the leader, auto-battle on: the Mug at an enemy; a Mug aimed at the character itself turned on an enemy); veldt and monster_box green; saves byte-identical after the guard. Not reproduced headless: the game's Defend swap itself (the test's character kept a Fight-like command), a Gau back from a Leap. Installed on the PC and the Deck (checksums match). The Deck's last save (11-10 11:50) predates the Veldt run: nothing saved with the Blackjack at (1, 1) | ~99% | 81% |
| 2026-11-07 | **The Whelk, played (the user: "play the whelk battle. its so wrong"); monster specials were other monsters' abilities; BNW's own event fights.** (1) Played headless: the shell hid the head and never brought it back, its idle turn hurt itself (50,000 -> 49,996), which arms the PR script's counter (it counters only below full HP), so every attack went to the shell and its Mega Volt counter (level 99, Magic 255: BNW's shell stats) killed a character a hit: the party wiped. Cause: port_specials took ability 377 + monster index as each monster's special, true for 25 of 384; it rewrote 236 abilities, 218 other monsters' specials and 18 of the PR's battle-event abilities: 696 (the AIs' idle turn, so every idling monster hit itself), 735 (SM_ERASER: everyone vanishes, how a boss's death ends a fight), 738/776 (the Whelk's head shown/hidden). Now each monster's special comes from the PR's own data (port_specials.pr_specials: the type-3 MSG_MON_ATK ability its AI script and Control group use: 353 monsters, 299 abilities), rebuilt in place when every monster sharing it gets the same BNW byte, else on a copy (20 copies from 7700: 390 "Hit" serves ~40 bosses that BNW gave 11 different specials); the AI compiler, Rages, Sketches, the plugin's Spirit-evade list and the probe's multipliers use the same map; abilities that aren't specials are never touched, names renamed only where every sharer agrees. (2) The Whelk now runs BNW's AI (monstBattleScripts #256/#308; BNW gave the vanilla head-in/head-out routine to its new Chesticle): the head stays out and attacks, the shell idles and counters any attack with Discharge (B7 -> PR 320), the head's death makes everything vanish. The compiler drops BNW's "event 5" because the PR's battle script sc_b_0011 plays that talk ("whatever you do, don't attack the shell!") and the head is on screen from the start (port_ai INTRO_SCRIPTS); a monster whose SNES main never attacks idles (696) instead of the compiler's default attack. (3) BNW's event fights (port_events.py): the SNES calls them through two-formation packs, BNW changed which formations 32 of the PR's event parties' packs hold; 21 fight BNW's line-up on the event's own look and battle script (parties 7000 + the PR's; 55 scripts call them): the opening's Narshe fight (2 Silver Lobos + 2 Guards), the Imperial Camp's Grunts and Dobermans (the PR's had faced Banshees: BNW moved its Doberman), the Phantom Train's ghosts, the rats, Owzer's Still Life (Nightshades), the Balloons (Grenades), the Sp Forces, the Serpent Trench, Phunbaba, the Cranes, Mega Armor fights; 11 keep the PR's (monsters brought in later: the Lobo pack, Merchant, Guardian, FlameEater, Stooges, Ultros; ambiguous: SoulDancer; not the same fight: Dark Side, ProtoArmor; BNW's formation empty: Umaro; own battle script: Air Force). (4) Harness: warp, script, slots, log (battle log), title load (stalls: below), probe_headless one run at a time | offline: 87 Python tests (new: event abilities untouched, every BNW special on its monsters' own ability, the Whelk's AI; event fights). In game (headless, saves byte-identical after each run): the Whelk won 3 times (a Magitek beam kills the 250-HP head, everything vanishes, the shell at 50,000), the opening's Narshe fight with BNW's 2 Lobos + 2 Guards. Not done: hitting the shell alone (the Discharge counter; aiming at a chosen monster isn't worked out). A run overlapping another's cleanup let Steam relaunch one game headless without the test's save block for ~2 minutes: stopped, every save file's checksum unchanged (a settings file and Steam's cloud marker rewritten with the same bytes); now guarded. Installed on the PC and the Deck (checksums match) | ~99% | 81% |
| 2026-11-07 | **The Whelk died at once (the user's report); BNW's emptied monster slots.** BNW's Whelk shell (SNES 256) has 1 HP, level 99, 255 defense; the port keeps the PR's own Whelk AI, whose shell ends the battle when it dies (FinalAttackAct 735: in vanilla it has 50,000 HP), so the first hit won the fight. Found with it: BNW moved or dropped monsters to other slots (Wrexsoul 290 -> 13, Colossus 310 -> 174...), emptying 36 vanilla slots that the PR's own event battles and kept encounter sets still fight by id (Wrexsoul, the South Figaro soldiers, Still Life, the Cranes, Phunbaba...): ported as 0-HP, 0-stat monsters with an empty AI. Now a slot BNW emptied keeps the PR's monster (stats, resistances, AI, special: ff6snes.blank_enemy), and a 0/1-HP placeholder keeps the PR's HP (16: both shells, the Guardian, Nimufu, Lakshmi, Ziegfried, event monsters). Next (not done): BNW's own event fights. The SNES calls them through two-formation packs, and the packs map the PR's event parties to BNW's formations (the Grunts -> Phalanx and Soldiers, Balloons -> Grenades, Still Life -> Nightshades, Wrexsoul -> Wrexsoul + Soulblazer; Kefka's and Umaro's packs need a look) | offline: 83 Python tests; harness: the Whelk fight (party 433) starts with the shell at 50,000 HP and the head at 250 (it had 1); a full headless fight froze after its opening tutorial (not verified to the end). Saves untouched (checksums). Installed on the PC and, once it woke, the Deck (485 mod files and the plugin, checksums match; the Deck is BNW only now, WC and its 8 seeds on its shelf) | ~99% | 81% |
| 2026-11-07 | **Biggs and Wedge's spells, a game harness, saves kept out of tests, WC and BNW split (plugin v0.22.0).** The user: Biggs and Wedge still learned every spell after the first battle. Shortcuts didn't reproduce it, so (the user's call) a harness now plays the game headless from the shell (GameHarness.cs, tools/game_harness.py: state, party, new game, presses through the game's own input checks). Played: a new game, the opening, Narshe's guards, the victory screen: Biggs and Wedge got entries for 19 spells. Cause: the port parked unused magicite-learning rows at value1 0, and the game reads a character's magicite spells as type-2 rows with value1 = the equipped magicite, 0 without one (MagicStoneData.GetLearnableAbilityList, no job check); Terra starts with entries for every spell, so only Biggs and Wedge changed. Leftover learning rows now get type 0, which no lookup reads (the earlier fix's level-255 rows too); replayed: no entries. Found on the way: the shop/box checks' warps had autosaved the PC's slot 21 (an opening save); every test run now blocks the game's saves, autosaves, bestiary and config (a full harness battle left the save folder byte-identical). Then the user: "WC/BNW need to be separate mods entirely": one mod per install (deploy.sh --bnw / --wc, the other mod's plugin to BepInEx/mod-shelf with WC's seeds), no Mods screen or mode switch, a package per mod (tools/package_mod.py) | offline: 83 Python tests (the learning test now covers magicite and equipment rows), 31 C# rule tests; the WC flag tests' moogle cup preset fails on -wmhc (WC 1.4.3's preset uses a flag its flag list lacks; nothing here touches flags). In game: the harness's first battle, twice. PC: BNW only, WC on the shelf with its 8 seeds, Magicite's standard folder, BNW verified loading alone. Deck: the spell fix, then the split with the next batch (BNW only, WC and its 8 seeds on its shelf) | ~99% | 81% |
| 2026-11-07 | **BNW fixes from the user's new game, Doma Castle's merchant, BNW's last chest changes (plugin v0.22.0).** The user's report: Magitek unusable, and every spell learned after the first battle. Spells: the learning table's parked rows (spells no one learns by level) sat at job 0 / level 0, which the PR reads as every character at level 0; now level 255 (port_spells.neutralize). Magitek: BNW's spell slots 131-138 (its Rage attacks) overwrote the PR's Magitek abilities 218-225; they get rows of their own now (6131-6137, profile spell_relocations), and Magitek keeps the PR's eight. A save made before the fix keeps its learned spells (a new game is clean). Doma Castle's merchant (BNW's shop 86, WoR): an NPC in the PR room, its script in the map's package.json (a map loads only the scripts listed there; the first headless run didn't find it), the stock opened through the plugin's SysCall "BNW:shop 93". Chests: of the six BNW changes left, the Moogle Charm only moved two tiles (the PR's chest stays: the new spot is inside the PR room's wall), the 1000 gil and map 414 ones are SNES copies of chests already ported, and the Ancient Castle box moved to its map's twin with the same fight (port_chests.moved_chests: the same event bit); South Figaro's Earring (both PR copies) and Mt. Zozo's Red Cap are boxes now (new box scripts on their map's battle background). Found on the way: boxes fought vanilla's formation of the same number, but BNW renumbers formations (port_chests.seed_party): 2 boxes now fight BNW's monsters (Narshe's Dinosaur, Tonberry), and 4 the port had switched to unrelated fights are back on their own (Telstar, Chesticle, Colossus, Master T) | offline: 83 Python tests (new: every box fights BNW's line-up; the merchant can be walked up to), 31 C# rule tests; headless: the merchant's shop opens with BNW's stock (warp, its script, the shop screen), the Mt. Zozo and South Figaro boxes start their battles with BNW's monsters (Bonelord, Phantom). Installed on the PC and the Deck (pushed once the Deck woke: 518 mod files and the plugin, checksums match the PC's) | ~99% | 81% |
| 2026-11-07 | **BNW evasion check, Scan, dance menus (plugin v0.21.0); the Deck caught up.** Evasion: Image breaking on 1 in 3 dodges was already ported (profiles.py condition_tuning: the PR keeps it as data, condition 19 until_type 16 HitAndProbability, until_value 25 -> 33). BNW's other evade changes (c2.asm C2232C-C22372: a covering knight's Evade halved, no Evade penalty from Rerise, no hit bonuses from statuses) and Interceptor not blocking a covered hit are left: the PR's hit calculation keeps them deeper than its decompiled parts show. Menus and battle UI (BNW readme "Scan Improvements", "Menu Improvements"): most of BNW's menu hacks fix the SNES screens the PR redrew anyway (status and equip screens, MP costs, Blitz names, spell icons, ATB colours: done earlier). New: Scan draws no counterattack (SpecialCommands, CounterModule.SetCounter skips it; its free turn was already in); each dance's description lists its four moves under BNW's names (port_dances.describe_steps, now on for BNW: profile dance_descriptions). The PR's Scan already has a status step (ScanBattleFunction.AddCondiitonMessage). Left: BNW's "Fail" / "Null" miss messages, and the Rage menu's two attacks (the PR's monster rows have no description to put them in) | offline: 79 Python tests (new: the dance descriptions), 31 C# rule tests; headless: v0.21.0 loads, its startup checks and the cover self-test pass. Installed on the PC and the Deck (v0.18-v0.21 reached the Deck today; checksums match) | ~99% | 80% |
| 2026-11-07 | **BNW battle engine: True Knight cover, Sketch's Magic, Umaro (plugin v0.20.0).** (1) True Knight (`CoverRules.cs`, config [Battle] RelicRules): BNW covers a Near Fatal ally (vanilla's rule) and also a back-row ally with chance Stamina / 192 per knight (Stamina / 96 while defending), only from the front row, never by a Near Fatal knight unless the ally is too, the knight with the most HP, single-target attacks only, a long status blacklist, no monster guards (c2.asm C2123B). The PR picks a single target's guard in TargetFunction.ProtectExtractTarget (a knight = condition type 606 TipsForKnights, the port's wear condition 29 for the relic bit, weapons too); a postfix there redoes the choice by BNW's rules whenever a knight stands among the target's allies, through the game's own SetProtectData (the act then targets the knight, as the game's own cover does), and drops the knight's Defend as BNW does. The choice itself is BattleMath.CoverGuard (offline-tested). Found on the way: the PR's ProtectTarget runs only for jobs with innate cover (none in FF6), so the first hook there never ran in a real fight; the probe self-test now goes through the real entry. (2) Sketch: a sketched physical attack takes Relm's Magic for Vigor (c2.asm SketchMag, doubled in 8 bits as BNW does: Magic 128+ wraps); the PR already uses her level and Battle Power. (3) Umaro: Tackle already respects row in the PR (it only ignores row for Blitzes and tools) and Blizzard's Blind already skips any Stamina check (the game has none; the plugin's Spirit evasion leaves Blizzard out), so only the toss of Mog as a sure critical is left (the damage step doesn't know who was thrown) | offline: 78 Python tests (new: every True Knight item gives the knight's condition), 31 C# rule tests (new: cover choice, Sketch's Magic); headless (probe battle, self-test through ProtectExtractTarget): a Near Fatal ally covered by the front-row knight by both the game's rules and BNW's (the act retargets to the knight), a healthy front-row ally not; the back-row Stamina cover and the Sketch change need a real fight. PC installed; Deck asleep (push pending) | ~99% | 79% |
| 2026-11-07 | **BNW battle engine: Leap, Mimic, X-Magic (plugin v0.19.0, `SpecialCommands.cs`, config [Battle] CommandLimits).** (1) Leap (PR command 24, ability 16 "Dive"): BNW offers it only in a leapable fight (the formation's own Veldt flag, wherever it's fought: c2.asm ChkMenu_Leap) and only while a second party member stands (C23B71 "Cannot Leap" alone). The PR's battle menu asks CommandAvailableData.IsAvailable (it greys Morph out that way); Leap now answers no unless the fight's party is on the loaded Veldt table (port_veldt lists every PR party of each of BNW's 74 Veldt formations: 94 parties) and someone else stands; event battles (no party set) keep only the second-member rule. (2) Mimic: BNW wipes Mimic's memory after each Mimic (C2/0224 ResetMimic), so Gogo can't mimic his own Mimic. The PR walks its act log back to the last act it may copy and skips only the Mimic command itself (its exclusion list, read in game: commands 2,4,5,6,8,9,24,25), not the act a Mimic made; when that act is a copy (BattleActData.isCopiedAct) Gogo now fights, the game's own fallback. (3) X-Magic: the PR gives each of its two spells (ContinuousMagicFirst / Second acts) its own counterattacks (CounterModule.SetCounter per target, per act); BNW allows one per monster per X-Magic (C20084 MayReset): a unit that counters the first spell doesn't counter the second. Found with the call scanner and three Ghidra runs (tools/decomp/leap_mimic, mimic2; the script now writes compiler-made <>c classes on Windows, which also unblocks True Knight cover's lambdas) | offline: 77 Python tests (new: every PR party of each Veldt formation is on the table, none of another), 30 C# rule tests; headless: the patches install, the limits line reads the game's Mimic exclusions and 94 Veldt parties, and in the probe's battle the game's own command check runs the Leap rule (3 standing, Leap on; that battle sets no party, like an event). Mimic and X-Magic need a real Gogo / X-Magic fight. PC installed; Deck asleep (push pending) | ~99% | 78% |
| 2026-11-06 | **BNW battle engine: the two-handed grip and Shock (plugin v0.18.0).** (1) The grip: the PR doubles a character's whole attack (PlayerCharacterParameter.ConfirmedAttack / GetArmConfirmedAttack x EquipmentAbilityData.TwoHandedMagnification = 2 when OwnedCharacterData.HasTwohand: ability 251 and one grippable weapon with the other hand empty; found by scanning GameAssembly.dll for direct calls, decompiled in tools/decomp/twohand), so BNW's spears, katanas and Excalibur (the only items giving 251 in the port) hit x2 where BNW gives Battle Power x1.5. The plugin keeps the game's check and swaps the factor: while the game computes the attack, HasTwohand answers no, and the result gets x1.5. Bushido x1.5 with any grip weapon worn (c2.asm C2/185B checks only the relic bit, shield or not); the Windslash / Aero procs x1.5 when gripped, else x0.75 (c2.asm Wpn_Chk; bnw_data weapon_flag_abilities). (2) Shock (Leo's, and Celes's with Leo's Crest, which turns Runic into Shock): the PR's Shock (ability 19, type 618) was a plain magic hit (power 128 vs Magic Defense); now BNW's 3/4 (Level x Stamina + current HP) in the SNES's 16-bit steps (a sum past 65535 wraps, as in BNW: Level 99, Stamina 128 at 9999 HP hits for 619), halved from the back row, ignoring defense (port: content flag group 11), and the user loses max HP / 8 once per use (KO through the game's condition controller if it empties the HP; no damage number). Left out: Sketch's Magic for Vigor (niche); next: cover/evasion extras, Leap, X-Magic, Mimic | offline: 76 Python tests (new: the grip weapons, Shock's data), 30 C# rule tests (new: Shock's 16-bit steps, the grip); headless formula probe (3.06 M calls, 0 errors): Shock passes all 79 cases on BNW's formula, Aero x0.75 passes; vanilla/BNW fail 6 each (the known Meltdown quirk and two monster->character cases at the 9999 cap, untouched by this batch); the grip hooks install and stay out of the probe; the grip itself and Shock's HP cost need a real fight. PC installed; Deck asleep (push pending) | ~99% | 78% |
| 2026-11-06 | **BNW re-ported with today's porter; its Veldt rebuilt.** The Veldt: the PR keeps it as a table (the misc asset's beast_field_encount_table: vanilla's 371 formations with vanilla's monsters), so BNW's Veldt offered vanilla fights. tools/port_veldt.py writes the mod's own: the 74 formations BNW's flags allow there (27 groups of 8, as the SNES cycles them), each fighting the mod's PR party and opened by every PR party of that formation; 27 parties made where no encounter set needed one (one for a formation vanilla's PR has no party for). Also in BNW now: each formation on its own battle background (port_encounters), the PR's scripted chests filled with the game's own ops (no WC plugin in BNW), 6 monster boxes with BNW's monsters on their box's background, South Figaro WoR's item shop with BNW's stock (82 shops matched + 10 PR copies). Left: BNW's new Doma Castle merchant (WoR, SNES shop 86, an NPC BNW added: the PR has no shopkeeper there), 4 chests BNW added (Molulu's Charm, map 44; 1000 gil, map 72; a box, map 408; Gauntlet, map 414) and 2 item chests BNW made boxes (South Figaro's Earring, map 179's Red Cap) | offline: 74 Python tests (new: every Veldt entry fights the formation's line-up); headless: the game loads BNW's Veldt (27 groups, 74 formations; the plugin's new [veldt] startup line); deployed to the PC and the Deck | ~99% | 77% |
| 2026-11-06 | **-fbs: Kefka's Tower's boss skips (both presets).** WC blocks two ways past bosses with invisible NPCs (event/kefka_tower.py). Poltergeist (the PR's Demon statue): the one-way ledge on map 564 (SNES 287, the same tiles) drops from the door to map 570 into the statue's side; in a WC game with -fbs, until the Demon is beaten (ScenarioFlag2 238), the ledge's own script (sc_e_0968, the map 564 copy: the main tower map has another ledge of that name) sets the party back a tile (wc_events.kt_demon_drop; the plugin's WC:has answers whether the seed has a flag). Inferno: the PR's walls leave no way round its one-tile trigger (map 544, GroundCollision), so nothing is needed. Also found: the options screen's other bug fixes are the PR's own (Evade for physical attacks, healing text color) | offline: 72 Python tests (new: the ledge with and without -fbs, standing and beaten); headless: kt-skip (new: onto the ledge with the Demon standing: set back to the door; beaten: the drop), kefka-tower green | ~99% | 77% |
| 2026-11-06 | **Cosmetic options left out (the user's call: no bearing on play).** Off the in-game options screen (tools/wc_flags_export.py COSMETIC_LEFT_OUT; 253 -> 238 flags): -cpal, -cspp, -wmhc (SNES palettes, the SNES minimap), -ahtc, -npctips, the ten Y NPC options. The screen keeps any flag it doesn't offer as typed (both presets carry -wmhc), so preset and pasted seeds stay the website's. -frw/-frm stay (story-scene flashes) with help saying so; -name, -cspr/-cpor (the PR's own art) and -ond stay. Graphics 'partly', Misc. 'yes' on the screen | 71 Python tests | ~99% | 77% |
| 2026-11-06 | **WC's start state audited; -ond, -nxppd.** (1) Story flags: WC's start sets 145 story and NPC bits over 36 places (event/start.py and each event's init_event_bits). A headless tour (tour-wob, tour-wor) walks into 51 towns and dungeons in a fresh WC game: none plays a scene or a story line on arrival, and of their 436 scripted entities none starts an unhandled story scene (the rest are talk, examine and vanilla traversal: Baren Falls' jump, which WC keeps too). With START_FLAGS and every check's own hooks (62 checks headless), the story-flag estimate goes from ~65% to ~90%; left: NPCs in town that WC shows or hides, not verified one by one. (2) -ond: the menus' job title shows the original name of each character the seed renamed (-name) or gave another's sprite (-cspr); the others keep their title (both presets set -ond). (3) -nxppd (both presets and the user's seeds; never done before): a battle's EXP isn't split between the party: the PR divides it by the characters the battle passes (BattleResultData.DistributionExpCount) before working out level-ups, so the total is multiplied by that count first | headless: tour-wob, tour-wor (the audit), nxppd (new: the PR's own battle result, 120 EXP at x3: every standing character gets 360, with 3 standing and with 2), battles, features, objectives green; -ond on a scratch seed (Terra and Locke renamed: their titles read Terra and Locke, the rest unchanged); 71 Python tests | ~98% | 77% |
| 2026-11-06 | **WC's party pick on character rewards.** WC opens its party select after every character a check gives (RecruitAndSelectParty, 31 events); the port gave the character and nothing more, so a recruit joining a full party sat in reserve until the airship. Every reward line (give(), and Gau's father's house and Kohlingen, which build their own) clears ScenarioFlag4 99 first; the plugin's WC:reward sets it for a character; then the PR's own one-party select screen (OrganizeOneParty, wrapped as its party points wrap it: black, the field's figures hidden) and the leader redrawn where the party stands (WC:party_refresh: FieldController.ChangePartyEntityAsset; the PR's points reload the map instead, which would cut a check's scene short). The event scripts are shared by every seed: games under way get it | offline: 71 Python tests (new: every one of the 65 reward scripts has the pick, no map change in it). Headless (a test can't work a menu: a test run stands in for the player and puts the recruit in front, swapping them in when the party was full): Kohlingen, Zone Eater (Shadow into a full party), Mt. Zozo, Phoenix Cave (its two parties) each show the recruit leading right after the pick and the screen as at the reward line; checks-wob, checks-more, sealed-gate, narshe-battle, south-figaro, celes, tzen, objectives green. Not seen by a test: the select screen itself. Installed on the PC and the Deck | ~97% | 77% |
| 2026-11-06 | **Playtest fixes, round 5 (Deck seed x9ezmvsqxxm7).** (1) Nothing to fight on the Veldt: the PR opens a Veldt fight when its party is met elsewhere (the misc asset's beast_field_encount_table names vanilla parties; BeastFieldEncount.EncounterMonsterParty), and since round 4 a WC seed's encounters are copies of a formation's party, one per area look (2000+), so meeting them opened nothing (the user's seed: 76 of 752 encounter slots could open a Veldt fight, vanilla 707). The plugin counts a copy as its formation's own party there (WcVeldt; event battles' copies by the seed's look_sources): 716 of 752. Works on existing seeds; battles fought before the fix don't count (the bestiary is shared by every save, so no backfill). BNW's next re-port has the same copies: its plugin needs the same hook. (2) Setzer learning Arise after his first battle: WC's roll, not a bug (-nm1 random -rnl1 -rns1: Terra's natural magic went to Setzer with random spells at random levels, Life 2 = the PR's Arise at level 4; WC itself, run on the SNES ROM with the seed and flags, gives the same hash and lists; -xpm 3 got him there in one battle). (3) Monster boxes, finished: a box whose pack WC changed in place (-fer, both presets) is rewired too, and the plugin's event remap (by party) leaves the boxes' scripts alone (the seed's boxes list) | offline: 70 Python tests; headless: veldt (new: a per-area copy met, its formation's Veldt fight open), monster-box (new: Narshe's box fights the seed's party 2061 on the box's background 5: a script battle is built with background 0 and takes its party's), battles, features, checks-wob. Installed on the PC and the Deck | ~97% | 77% |
| 2026-11-06 | **WC gaps, in order: the Imperial Camp, MagiTek, chests.** (1) The camp stays on the world map after its check (the reward no longer sets the escape's end, 97) and loads plain then: its chests and exits, no scene; its 4th chest (the Star Pendant tent's) is there in every WC state (vanilla shows it after a scene WC skips; WC sets its bit at the start). (2) MagiTek dealt as a command (-com): the seed names those characters (wc_seed.json "magitek"), the port gives them one Magitek slot (not the armor's hidden one too), the plugin lists and allows command 26 for them anywhere (WcMagitek); Terra starts with WC's basic 4 moves like everyone (her other 4 with "Magitek Upgrade"). Older seeds name nobody. (3) Chests: the PR keeps the SNES's empty chests as scripted "Empty!" objects (ev_e_no_treasure_N), which the chest scan skipped: they are chests to the matcher now, and a seed that fills one rewrites its script (the plugin's WC:item / WC:gil give it, as a check's reward does): South Figaro's basement, Albrook's two, Phoenix Cave's five, 11 PR copies. WC's Thamasa chests (moved from the unused pre-Leo map onto the town's two maps) land in the PR's Thamasa chests. The matcher votes a map's offset over all its layers (one-chest layers had swapped Phoenix Cave's two floors) and prefers an unclaimed entity whose contents agree (the Narshe mines' two versions). All 11 monster-in-a-box chests pair with the PR's boxes now (six had no pair: the PR renumbered the boxes' flags and their maps hold no other chest to line up with; Narshe's Lobo box had gone to the Lone Wolf chest, which WC never changes): by the party they fight, else as the only box in their area; Kefka's Tower's two fight through EncountBoss; a box whose pack WC changed in place (-fer) counts as changed too, and the plugin's event remap leaves box scripts alone. A box with WC's shuffled monsters (-cms, in both presets) fights them on the box's own background with the formation's music, as the SNES does (a copy of the party under the per-formation ids the AI's party checks know). The Phantom Train's 3: the PR keeps chests 106 and 109 as Tent pick-ups on its luggage racks (scripted, like the empty chests: filled per seed; a map-size tie-break tells its two cars apart, the sizes shipped with the generator, which carries no tilemaps) and dropped 110 beside 109: its contents join that pick-up | offline: 70 Python tests (test_wc: the camp re-entry and its chest; test_wc_xref's chests now compare the filled empties, Thamasa, and every shuffled box's battle and background on the default and the user's seed); headless: train_chest (the user's seed: the rack gives its Fake Mustache and X-Potion, as WC's SNES game; the bundled generator had crossed the two cars until it shipped the map sizes, bundle check now green), the camp (chest there, back in after the check: plain, the entrance kept), magitek (listed and usable for Terra only, one slot, 4 moves), checks-wob with empty_chest (South Figaro's basement gave the seed's Hero's Ring and its line), bundle parity. Installed on the PC and the Deck | ~97% | 77% |
| 2026-11-06 | **Playtest fixes, round 4 (Deck).** (1) The Imperial Camp's walk: in WC the trigger down the camp's middle (36, 23) runs Kefka's dash and "Cyan rushes in", which puts the party by Cyan's duels (8, 25; decoded from the SNES event, LoadMap 0x77). The PR camp in WC's state had no trigger there, so the party walked across the whole camp. The PR's own trigger on that tile (Leo's talk layer) is set in a WC game, its Leo and soldiers hidden, and takes the party to the duels once a visit. (2) Battles on the wrong background: a PR battle (monster_party) holds its background and music, the SNES takes the background from the map. The porter made one PR battle per formation, so a formation shuffled into several areas showed the first area's (the Crumbling House fought on the factory's background, to battle music the house doesn't play). Now one per formation and area look (the area's vanilla background and music; 200 more in the user's seed), and a swapped event battle (boss shuffles, the Burning House flames) fights on its event's background with its formation's music (a boss keeps its theme): 75 of the user's seed's 91. (3) A line shown before skipped story lines left its window open into the next scene (the Collapsing House door: the leader's "Let me help you!" then Sabin's skipped lines): a skipped line ending its run closes a window left open | offline: 70 Python tests (new: the camp walk, battle backgrounds in test_wc_xref on the default and the user's seed, event battle ids). Headless: features, battles, checks-wob, checks-more (now with the camp walk: the party from the entrance to (8, 25)), objectives, seeds: green. The objectives seed first failed to generate (a formation fought in 6 area looks reached the event range: event battles now take free ids from 20000). Not covered headless: the message window (tests never show one). Installed on the PC and the Deck | ~97% | 77% |
| 2026-11-06 | **Playtest fixes, round 3 (Deck seed fb0zj6u8kmxd).** (1) Start HP/MP: the PR's JoinCharacter gives a first-time joiner the HP/MP growth of a level it picks itself, kept apart from the level (AddtionalMaxHp/Mp): every WC recruit had hidden extra HP/MP (Sabin 86/209 at level 3, Gau 70/167, Mog 70/121; the SNES game, run in Mesen with the same seed: 86/86, 70/70, 70/70). A WC character's first join drops it; -sal still raises to WC's average level after. (2) Every rage at the start: the PR gives Gau an entry for every rage (unknown ones at skill 0) and the skill sharing counted any entry as known (252 taught to the seed's Rage holder). Known = learned to 100; with -srr/-slr/-sdr a joiner's own starting rages/lores/dances (vanilla's 9 rages for Gau) give way to the seed's, as WC clears those bits. (3) Black screen after the Imperial Camp: the reward's line opened on the battle's black screen (vanilla fades in later in that script) and waited for a press nobody could see; the jump to the world map also lacked the camp exit's arrival script. Now: fade in, the reward, the camp's own exit (sc_e_0133_1). A new offline test runs every reward script through the fades: 4 more did the same (Collapsing House, Sealed Gate, Burning House, the moogle defense: vanilla joins characters there in the dark), fixed. (4) Narshe's security checkpoint: WC deletes its explanation (Edgar's line); in a WC game the PR's three lines are gone, the route of light and the trial stay. (5) Check NPCs look like their reward (21 SNES NPCs on 24 PR map entities, tools/wc_npcs.py; the Celes test checks hers in game). (6) The in-game seed list: Delete (Left/Right on an earlier seed, A to confirm twice; never the current seed or one a save uses). (7) Found by the new checks: a -chrm monster box whose party a shuffled event battle also names fought that event's replacement (an empty battle): boxes skip the event remap now; a script's autosave that won't save (test runs, -nosaves) ends at once; the headless black-screen check reads the fade overlay's alpha (the fade manager's flags stay "out" after the Blackjack's boarding arrival on a deck that shows fine), with a self-test that a forced fade reads black | the user's seed cross-referenced with the SNES game (test_wc_xref now takes any seed: FF6_XREF_SEED/FF6_XREF_FLAGS): WC's hash, every check's reward, every changed chest, every character's commands, every esper's spells and bonus, every shop, the start (party, gil, items, levels, now HP/MP in Mesen), the check NPCs' art: all 8 match (the esper test had read an unchanged bonus as missing: fixed). Offline: 67 Python tests (test_wc 26, new: the camp's exit, the checkpoint, no reward line on a black screen) and the C# tests. Headless, 18 scenarios green: the user's seed (start HP/MP full, start rages = the seed's 30, the deck not black), celes, features, objectives, checks-wob, checks-more (every check's screen faded in after its reward), seed-delete, narshe-wob, south-figaro, worlds, worlds-back, startup, sprites, battles, sealed-gate, narshe-battle, seeds, and the fade self-test. Installed on the PC; the Deck was asleep | ~97% | 77% |
| 2026-11-06 | **The Worlds Collide generator is ROM-free.** It no longer carries the SNES ROM or reads one: WC's input is `data/wc_base.bin` (94,656 bytes of the ROM's data tables, names and enemy AI scripts, 44 KB compressed; no graphics, dialogue, descriptions, maps, music or game code; made from the ROM on the dev PC by `tools/wc_basedata.py`), with `tools/wc_romfree.py`'s stand-ins for what WC only rewrites, WC's own code unchanged. WC's output stays in the generator's memory and work folder (compressed, never an .sfc), the porter reads the same input. The bundle refuses to build with an .sfc in it | the same seed folder as with the real ROM (every table, message, file, the seed info, WC's log) for 5 of 5 configurations (both presets, spoiler log, open world + random bosses + permadeath + NPC tips + original names + custom sprites, the -chrm/-csb/-eer/-scan/-nee batch); `tests/test_wc_romfree.py` (parity, the data is the ROM's, no ROM written) and the cross-reference tests now run it; 63 Python tests pass; installed on the PC and the Deck, no ROM in either game folder | ~97% | 77% |
| 2026-11-06 | **SNES ↔ PR cross-reference tests, Celes, chests.** (1) Freeing Celes is WC's now: talking to her gives the reward at once (the vanilla "(Remove her bindings.)" question, Locke and Celes' walk and the heal are gone; the story skip had only hidden their lines). (2) `pr/tests/test_wc_xref.py`: one seed made by WC's own wc.py (its SNES ROM and spoiler log in the test's temp folder, deleted after) and by our generator, compared: WC's hash, every check's reward (WC's log, by name), every character's commands, every esper's spells/rates/bonus, every shop's stock, every chest WC changed, and the start in the running SNES game (Mesen 2, headless: no window, no sound; `tools/mesen2`). (3) The chest test found 57 of the user's seed's changed chests keeping vanilla contents: South Figaro's 8 outside chests (its WoB and WoR versions share flags in both games; one SNES chest per PR flag gave both the WoR's), Narshe's houses and the mines (chests in event layers, `ev_e_*.json`, which the scan skipped), single-chest maps paired with other towns' houses. Chests are now matched per PR file and inside WC's chest areas (WC's area_chests joined to the PR's area.csv): the same seed changes 289 PR chests (was 274), 31 not placed (was 57: WC's own copies and unreachable ones, 17 SNES chests the PR has no chest for, WC's 10 new Thamasa chests). (4) A check's own scene shows no story lines at all now, a character's or not (Phoenix Cave's "Is that...magicite?", the Ancient Castle's queen): only the reward's line and a menu's question | offline 59 Python tests (7 cross-reference, incl. Mesen's), the C# tests; headless celes, south-figaro, rooms, startup, features, checks-wob, checks-more: 0 failures, and in the 15 checks of the last two only the reward lines would show in play | ~97% | 77% |
| 2026-11-06 | **Playtest fixes, round 2 (Deck).** (1) A restarted game ran its seed on vanilla data: the startup apply ran once the master manager said initialized but before its tables existed, every table failed and nothing retried (commands, shops, monsters, items vanilla; only maps and scripts the seed's). It now waits for the tables and retries. (2) A chest WC turned from gil into an item kept the gil message ("Obtained (NUM) gil!"): the message follows the contents (T0001_01/03/05; the plugin's -chrm boxes say "Monster-in-a-box!"). (3) Narshe's treasure room: its locked chest (and owner) appear only after a story flag WC's start leaves off (ScenarioFlag1 138): in a WC game they're there (no more walking through the chest). (4) Narshe's shops were the opening's (SNES shops WC calls inaccessible, never randomized): a WC game has the WoB ones (randomized). (5) South Figaro isn't occupied in WC (its start: after Locke's escape, Celes chained below): the start no longer sets the occupation; the basement keeps Celes for the check. (6) Shop copies: a town's two copies of a shop both get its stock (South Figaro WoR's item shop kept vanilla stock in one). (7) The generator's file move falls back to a copy across drives | headless: startup (a seed active at start: 0 failed, commands the seed's), rooms (treasure room chest, Narshe's WoB shop, South Figaro and Celes), the South Figaro check, Narshe WoB; offline 22 WC tests (chest messages on a real seed, shop copies, rooms) | ~97% | 77% |
| 2026-11-06 | WC's random Zozo clock (-rc, always on: WC's time from the seed; each hand's menu asks the plugin, WC's ten clues on the PR's NPC lines), "Perform In Opera" (the stage open in the WoB until performed and not during the disruption, failing is game over, done -> the quest: the 15th quest tracked), the story-text rule refined (a line is skipped only when a character speaks it: narration and a puzzle's answers stay), the Sealed Gate as WC has it (no Kefka scene and event battle: the gate opens, the reward, back to the cave; the PR's battle needed the party to fight it out, so its headless check never finished) | run_tests.ps1, all green: offline 47 Python tests (17 for WC) + the C# tests; headless 22 scenarios (New Game -> wheel -> landing -> Narshe WoB/WoR, world sync, both wheels, Atma, metamorph, the clock, features, sprites, bosses, objectives, all check groups, Tzen, seeds, menus) | ~97% | 77% |
| 2026-11-06 | **Playtest fixes (Deck).** (1) Every WC game began "after the collapse": the start's double-Morph flag, "ScenarioFlag1 297" (the game's own constant), is ScenarioFlag2 41 (the game keeps scenario flags in one 1024-flag array, 256 a segment): Narshe in its WoR state with WoR random fights in town, its exits to the WoR, no airship there. Double Morph is now a Morph hook (TranceConditionFunction.Start), saves from the bug are put back, and the world flags follow the world map you're on (WorldSync; your world's airship comes to you if it isn't on that map). (2) The -scis Sketch hook made UniqueUpdateParameterProvider.HitCalc an invalid program (its ref Dictionary parameters): every unique command (Runic, Slot, Steal...) threw; now on IsResistanceSketch. (3) No achievements in a WC game. (4) FPS: the title check searched the whole scene 3x a second; a [perf] line every 2 min shows the plugin's frame cost. (5) WC cuts the story's dialogue: event scenes' story lines skipped in a WC game (townsfolk, questions, rewards stay; [Story] SkipDialogue). (6) Seeds ~2.3x faster: the port runs in the generator's process, JSON written with the C encoder (json.dump to a file is pure Python), the vanilla scans cached (vcache.py, made in the bundle), files moved not copied. (7) Control's 100% hit (-scis: accuracy 255), metamorph sets under -pd/-nee (battle_extra_data, now a seed file). **Tests:** pr/tools/run_tests.ps1 runs everything: pr/tests/test_wc.py (the WC scripts in a model of the game's script machine and its flag array), the C# tests, and the headless scenarios, among them a New Game -> the wheel -> landing -> walking into Narshe (the WoB's) -> walking out (the WoB map, the Blackjack there), and the same through (Fly to the World of Ruin.) | headless: narshe-wob and narshe-wor 0 failures, world sync 0 failures; offline 39 + C# tests pass; the same seed byte-identical before/after the speedup (12.5 s -> 5.4 s on a busy PC) | ~97% | 77% |
| 2026-11-06 | **Fixed: a WC New Game started the vanilla opening** (the user's first playtest): the opening hook read the attract-demo variable backwards, and the headless tests started their New Game with title command 0 instead of NewGame, so they ran the demo path and never saw it. Now the title's NewGame command marks a New Game and WC:start decides; the tests use the real command. -saw (the Atma Weapon's fight result doubles, CalcControllerProvider.GetFightStatus), -llr Lv. 1/2 (LEVEL1/LEVEL2: the game reads "LEVEL" + n). Seed generation faster: the bundle ships precompiled bytecode (each seed recompiled Python + WC from source; worst under Wine on the Deck) and the chest scan runs once per seed | headless: a real New Game lands on the Blackjack with the seed's party; Atma attack 18.0 -> 34.4 damage (1.91x); seed generated in 9.4 s on the PC | ~97% | 77% |
| 2026-11-06 | WC wrap-up: -chrm (a chest WC made a monster-in-a-box opens empty and its battle starts), -llr (Lv. 3/4/5/? with the PR's level programs and names), Sketch tables final; -anca without a re-roll loop (the first version hung the auction in a headless run: caught before any play). BNW re-ported (its sketches) | headless: the option batch with -chrm/-csb (the box's battle started), the auction/Zozo/Kefka's Tower checks: 0 failures | ~96% | 77% |
| 2026-11-06 | WC round 2: Sketch abilities ported from the mod's table (WC's -scia, BNW's sketches too), -scis (Sketch's hit roll always hits; decompiled UniqueUpdateParameterProvider.HitCalc), -csb (the cursed shield's count: decompiled SystemCall.Current.CursedShield, DataStorage 8/9, 256), -lel (lores enemies use are learned without a Lore user), -ccrs (the n-th item / gil chest opened gives WC's n-th). Fixed: a hook lookup that matched two overloads stopped the whole plugin from loading (caught by the regression run before any play); the plugin now loads each option's hooks separately, so one failure only turns its own options off | headless regression of the auction, Zozo and Kefka's Tower checks; -ccrs/-csb exported from a generated seed | ~95% | 77% |
| 2026-11-06 | WC "everything but playtesting" pass (worklist below): boss/dragon/statue shuffles and random fixed encounters (event battles swap parties at Encount/EncountBoss), a proper SNES formation -> PR party map (~40 formations, many bosses, aren't at formation + 1), chests matched by place (re-bitted, empty, monster-in-a-box battles per seed), shop ties, esper learning/natural magic/ability scaling/starting equipment/event timers, skills follow commands, 14 of 15 quests, every objective result, recruits (-sal/-stl/-sn/-eu), battle commands (-fst/-brl/-rnl/-dns/-dda/-srr/-slr/-sdr), espers (-ebs/-ebr/-eer/-eebr/-ems), items (-stra and WC's item bits, equipment-taught spells, -ari/-anca), -scan, script flash removal, sprites/portraits (PR art only), permadeath, the Marshal's minions, seeds + save slots synced to the Deck. BNW re-ported (starting equipment, chests by place, AP, equipment spells, shop ties) | headless: boss swap, quest through a swapped boss, objectives (Learn Rages, Magitek Upgrade, Max Morph), the option batch (levels, start skills, esper rules, Umaro, weapon lists, dance text, auction, scan): 0 failures. Not played: the user's playthroughs | ~93% | 77% |
| 2026-11-05 | WC's Coliseum (-cor/-cosr/-crr/-crsr/-crvr...): the seed's opponent and reward for every wagered item. The PR builds its Coliseum in code (ColosseumInfomationData, Coliseum parties 1000+ holding copies of regular monsters); its lookups now answer from the seed, and opponents without a Coliseum copy fight in that wager's own Coliseum party with the monster swapped in | headless: 30 of 30 wagers answered from the seed, the game's own answers match the vanilla table (ids line up); 0 failures | ~86% | 77% |
| 2026-11-05 | WC objective results: Learn Spells (WC's per-seed spell order, read from its ROM), Boss Levels (WC's boss battles), Fallen One, Imp Song, Sour Mouth; WC's -nosaves (Ironmog: no Save/Quick Save in the field menu, no autosaves); -move is the PR's own dash / Auto-Dash setting | headless: each result applied, the field menu without Save; 0 failures | ~85% | 77% |
| 2026-11-05 | WC start-of-game state: **fixed: the Blackjack's deck had no wheel in a fresh WC game** (the PR places it only after a story flag WC games never set; my wheel tests ran its script directly); a WC New Game now sets 41 more flags (Narshe's opening over, South Figaro occupied, the Returners' hideout open, Sabin's scenario over, the opera's overture, Gestahl's dinner, no coin toss, tutorials off, Shadow's dreams, the WoR's Mobliz / Ebot's Rock / Ancient Castle states...); Narshe skips its story triggers in a WC game; Edgar's throne scene, the burning house's entrance and Esper Mountain's entrance are placed while their checks are open. Start a new WC game to get these | headless: the flags set (and 194/64 left clear), world switch both ways; 0 failures. The deck's wheel, Narshe and the placed entrances still want a look in play | ~84% | 77% |
| 2026-11-05 | WC bosses: the generator exports WC's 40 boss objectives with their PR battles; winning one counts it (TreasureFlag4, unused by vanilla there), for "Defeat N Bosses" / "Defeat <boss>", the Progress page and the bd scaling | headless: Track pages build with the boss data (62 of 62 checks); a won boss battle isn't testable headless | ~81% | 77% |
| 2026-11-05 | WC objective Auto-statuses: Haste, Regen, Float, Safe, Shell, Reflect, Image, Clear, Imp, Berserk, Condemned, Muddle, Mute, Sleep, Slow, Dark on the party at every battle's start once their objective is complete | headless: Auto Haste + Auto Regen applied in a battle; 0 failures | ~81% | 77% |
| 2026-11-05 | WC objective results: once an objective's conditions are met its result applies (saved): stat +/- (all, or one character, also to characters who join later), Learn SwdTechs/Blitzes/Lores/Dances (deferred until the character joins), item gifts (the items WC's generator picked), Full Heal, Enemy/Dragon/Final Levels (WC's scaling, which now also counts checks done and dragons defeated); a notice shows on screen. Also WC's -etr (no opera / collapsing house / Floating Continent countdowns) | headless: 5 objectives completed and applied, Cyan learned his on joining, +20 enemy levels in a battle; 0 failures | ~80% | 77% |
| 2026-11-05 | WC batch 10, the last four checks: Narshe Battle (Banon waits on the snowfield in the WoB; the scenario lineup does nothing; after Kefka: reward, one party picked, Arvis' house), Opera House (WC's start marks the performance done; no free Locke/Celes; after Ultros: reward, Blackjack deck), Imperial Camp (with Sabin: straight to Cyan's fights, no Leo/Kefka/poison/Doma fall; reward instead of the Magitek escape), Moogle Defense (entering the mines with Mog asks to defend; the party keeps its leader; after the Marshal: reward, one party picked) | headless: all 4 rewarded, 0 failures (Narshe Battle and Moogle Defense end in the one-party picker, which a windowless game can't answer) | ~78% | 77% |
| 2026-11-05 | WC batches 8-9 (the story checks): Floating Continent x3 (the Blackjack's wheel offers it again once Shadow is found, any party size; no Shadow leaving, no free Celes, no world collapse: the escape lands on the deck), Veldt Cave (no Thamasa flight), Lete River, Sealed Gate (no Imperial Base esper attack), Serpent Trench (+ Nikeah's plain ferry), Phantom Train, South Figaro Cave (Tunnel Armor while the check is open; occupied South Figaro kept), Doma Siege (starts on arriving with Cyan), Phoenix Cave (no Kohlingen flashback). Also WC's -scan/-warp (everyone knows Libra/Teleport) | headless, 0 failures: 10 of 11 rewarded (FC x3, Veldt Cave, Lete River, Serpent Trench, Phantom Train, South Figaro Cave, Doma Siege, Phoenix Cave), -scan/-warp; Sealed Gate built but untested (its scene map 606 never loads without graphics) | ~77% | 77% |
| 2026-11-05 | WC objective result "Unlock Final Kefka": the stairway into Kefka's room pushes the party back until that objective is complete (conditions the port can't track yet count as met, so no seed locks the ending); plugin v0.6.0 | headless: sealed with objective A unmet (party stays, message shown); 0 failures | ~71% | 77% |
| 2026-11-05 | WC objectives in the Track menu: each seed's objectives (exported from WC's generator) with live progress per condition (characters, espers, checks, dragons; bosses and quests not tracked yet); dragons on the Progress page; -hf hides the Flags page; Kefka's Tower (Ultima Buster) check | headless: Objectives page lists the seed's 5, Kefka's Tower rewarded; 0 failures | ~70% | 77% |
| 2026-11-05 | WC batch 7: the 8 dragons (one countdown hook; WC's Crusader/countdown removed), the Auction House (Golem and Zona Seeker sales give the seed's rewards, the auctioneer names them; WC's door esper hint), Kefka's Tower after the Ultima Buster (WC's Atma). Fixed: the Cultists' Tower top had its second reward on the Holy Dragon (given twice: the top chest already holds it); now the MagiMaster marks it done | 11 checks pass in one run (8 dragons, MagiMaster, both auction sales); Kefka's Tower and the hint ported, tested next batch | ~68% | 77% |
| 2026-11-05 | WC Track menu: a "Track" entry in the field menu (above Configuration) opens Checks (WC's 62, by gating character, done ones dimmed), Progress, Objectives, Flags; Whelk | headless: entry in the real field menu, pages build, Whelk rewarded | ~62% | 77% |
| 2026-11-05 | WC checks batch 6 (Strago): the Cultists' Tower (Strago at the foot, the top's treasure), Ebot's Rock (both Hidon versions), Thamasa's burning house (after the Flame Eater) | 4 checks pass (35 of 68 rewards) | ~60% | 77% |
| 2026-11-05 | WC checks batch 5: Narshe WoR's second reward (the smith gives both), the Magitek Factory's Number 024, Doma's Three Stooges, the Veldt (Gau's dried-meat scene; scenes in two maps now ported in every copy) | 4 checks pass (31 of 68 rewards) | ~58% | 77% |
| 2026-11-05 | WC checks batch 4: Lone Wolf (Mog + the Gold Hairpin's place), Baren Falls (where Gau is named; no free Cyan/Shadow on the jump), South Figaro, Figaro Castle WoB, Mt. Kolts, Daryl's tomb | 7 checks pass (27 of 68 rewards) | ~56% | 77% |
| 2026-11-05 | WC checks batch 3: Doma WoR (Cyan wakes, the throne), Magitek Factory (Ifrit/Shiva, the tube espers), Mobliz WoR (+ no Fenrir), Doom Gaze | 6 checks pass in one run (20 rewards done) | ~53% | 77% |
| 2026-11-05 | WC checks batch 2: Esper Mountain, Figaro Castle WoR, Tzen's collapsing house, Narshe WoR's smith | 13 scene checks pass in one headless run | ~50% | 77% |
| 2026-11-05 | WC checks batch 1: Owzer's Mansion, Zone Eater, Mt. Zozo, Umaro's cave; tests run several checks per game session | 9 checks pass in one run | ~48% | 77% |

**Now:** WC is at ~86%. Left: a playthrough of what headless tests can't reach (the deck's wheel
in a fresh game, Narshe's town, the routes into the story checks, Sealed Gate, the one-party
pickers, a won boss battle); objective results KT skip, Learn Rages, Forget Spells, Magitek Upgrade,
Max Morph, 4 Auto-statuses; the rest of
WC's start flags (Figaro WoR's cave path, the Nikeah boat); most assembly-only flags (-pd, flash
removal, NPC tips, the bug-fix and challenge patches).
**Then:** BNW leftovers (Leap, cover, X-Magic, Mimic, Gauntlet, menus, dialogue).

## WC worklist: everything but playtesting (2026-11-05, from a flag-by-flag review)

The user asked for everything except testing to be completed. A review of every WC option
(three research passes over WC's source, the porter and the PR's code) gave this list. Tick
items as they land; P0 first (they affect every or most seeds).

**P0: bugs / every seed**
- [x] Esper learning (port_spells LearningPorter): SNES learn rate as-is, all 5 slots, more rows when needed (was BNW's 100/cost rule, 3 slots)
- [x] Natural magic learners (-nm1/-nm2): WC moves them in assembly (JSR C0/A182); the rows go to the learner's job
- [x] Ability scaling (-ase/-asr, on in standard flags; 2026-11-11: picked when the monster acts, WC's formula and odds, WcAbilityScaling + ability rows 7600-7635; was one spell per script from the unscaled level). Left: the special-attack tiers (0x5E-0x60), bosses that keep the PR's script
- [x] Skills follow commands (Random Unique commands; WcSkills, -sel/-bel rows; headless ok): SwdTech/Blitz/Lore/Dance/Rage holders get learning rows + known skills copied; WcObjectives.Learn by command holder
- [x] Boss / dragon / statue shuffles (WcBattles: Encount/EncountBoss swap, headless ok) (-bbs/-bbr/-drloc/-bmbd/-stloc/-srp3; -fer event packs; -cms): remap by party id at Encount/EncountBoss (External.Misc.CallEncount), WC's replacements exported per seed
- [x] Starting equipment (port_characters, BNW too): PR slot 3 = body, 4 = head; empty = 93/198/199/200, not 0
- [x] -etr randomizes event timers (seconds from WC's ROM), -etn removes them (now -etr removes)
- [x] Chests: EMPTY (-cce), items in vanilla-empty chests, re-bitted chests (now matched by place), -cms monster chests (their battle scripts per seed). Left: -chrm (an item chest can't start a battle without a script), ~50 SNES chests the PR merged into one
- [x] Shops: identical-stock shops pair by their town's neighbours, PR copies (WoB/WoR) take the stock too, -ssf sell fraction
- [x] Morph: WC's double duration in the WoB (TranceConditionFunction rate 0.5 -> 1)
- [x] -escr (monster_party.not_escape from enemies' no_run), -bnu (undead: monster.species bit 0)

**P1: options**
- [x] Objective quests: 15 of 15 tracked (battle ones by the event's own party, so WC's swaps still count; Coliseum, auction, Suplex, Cid x2, Injured Lad, checkpoint and Zozo clock hooks; Perform In Opera by the performance's end, wc_events.opera_performance)
- [x] Objective results: Learn Rages, Magitek Upgrade, Max Morph (MagicLearningValue 255), Auto Dog Block/Life 3/Seizure/Overcast (headless ok)
- [x] -sal (joiners at the average level: growth-curve HP/MP, exp, level skills), -stl (start level, port), -eu (Umaro's Equip menu), -sn (port), -fst (Bushido ability_wait / 4), -brl (Duncan's scene waits), -slr/-srr/-sdr (start skills to the command holders), -rnl (rages after every win, no Leap), -dns (no stumble odds), -dda (dance help lists its steps). Left: -llr (level lores' levels)
- [ ] -scis (Sketch/Control always hit, controller stats), -scia (Sketch/Control abilities: tables): the PR's hit roll is battle code not located yet
- [x] -mpm morph gain, -nxppd (EXP not split) (WcRewards). Left: steal gaps (Thief Glove, empty rare)
- [x] -bmkl (the Marshal's minions: WC's formation 4 ported into parties 5/596 unless -bmkl), -dgne (ports itself: WC's no-run bit and AI)
- [x] Espers: -ebs/-ebr bonuses (WcEspers: MagicStoneData lookups answer as a vanilla esper with that bonus; Speed+2 / HP+100% scaled), -ems multi-summon. Left: -eer/-eebr restrictions
- [ ] Items: done -stra and WC's item bits (Runic/Bushido weapon lists per seed). Left: -csb (the PR counts in code), -saw divisor (the PR's Atma formula is code; WC's tier move is WC's own), item-taught spells (learning type 4), metamorph items, -ccrs scaled chests, -chrm random monster chests
- [x] Auction -ari (the four normal sales give and announce the seed's items; they also count for Win An Auction), -anca (the random pick draws from one fewer and its last value becomes the next sale: the Talkobo's and airship's share spread over the rest, as WC's re-roll, with no new loop)
- [x] -scan (monster flag 1 answers no, Libra 0 MP), flash removal (-frw/-frm: script flashes kept dark; battle animations are the PR's own art), -crm and -ahtc are the PR's own (rewards with ??? for hidden ones; heals in their own color). Left: -ond, -fbs (untested whether the PR's Kefka's Tower allows the skip)
- [x] Character sprites / portraits (-cspr/-cpor): character_asset per seed (battle, field, portrait) for WC ids with PR art (0-13, Soldier, Leo, Banon, Ghost, Kefka); WC's community sprites have no PR art
- [x] Permadeath (-pd): inns/tents/story heals skip fallen characters (FullRecoveryHpAndMp / RemoveAllCondititons / CanRecovery); WC's data (no Life spells, no Fenix Downs) ports itself
- [x] Cross-machine saves: deploy --deck syncs seeds and save-slot records both ways (tools/wc_sync_seeds.py; Steam Cloud carries only the saves)

**P2: large / low value**
- [x] -rc (Zozo clock: WC's time and clues, 2026-11-06)
- [x] -npctips, Y NPC (-y*): left out by choice (cosmetic; off the options screen, 2026-11-06)
- N/A in the PR (no work): -cpal, -cspp (SNES palettes), -wmhc (PR minimap), -emi (PR has it), -rr (deprecated), SNES bug fixes -fe/-fvd/-fs/-fedc/-fr/-fj/-fc (PR's own engine)

## Status (update after each milestone)

**Brave New World: ~81% complete** (2026-11-07, plugin v0.22.0). Weighted checklist:
weight = rough share of what makes BNW play differently from vanilla, done = judged
from the Plan at the bottom. "Shared" = how much of that area's work other mods reuse.

| Area | Weight | Done | Points | Shared |
|---|---:|---:|---:|---|
| Tooling: extract, porter, tests, plugin framework, PC + Deck install | 5 | 100% | 5.0 | all |
| Game data: characters, growth, equipment (incl. weapon procs, special effects, relic effects), items, spells, enemies (incl. special attacks), resistances, formations (each on its own background), encounters, the Veldt (the mod's 74 formations), shops (with BNW's new Doma Castle merchant), chests (layout-matched, both copies of shared places, the PR's scripted chests, monster boxes on BNW's own formations, chests BNW moved, item chests it made boxes), Rages | 25 | 100% | 25.0 | all (porter is mod-agnostic) |
| Enemy AI: 271 regular + 51 of 88 bosses on BNW's AI (the Whelk's shell and head added 2026-11-07, played); each monster's special its own (was another monster's for most: fixed 2026-11-07); 81 branches still skipped (was 189; FC 1B formation checks now compile); battle lines in compiled scripts (45); explicit targeting not ported (abilities' default targets used); 25 hybrids, only the Whelk played | 15 | 81% | 12.2 | most (compiler generic) |
| Esper system: restrictions, EP/EL, EL bank, equip bonuses, SP bank, displays | 10 | 95% | 9.5 | none (BNW only) |
| Battle engine: damage formulas (probe-checked), Steal, Regen/Poison/Sap, Reflect, Spirit evasion, turns, Dance, Bushido, GP Rain, Mantra, physical skills, Morph, character-on-character, encounter odds, Imp, Blind, Steal hit roll, Morph gauge, Tools, nATB (native) + status timers, Slot (reels, no rigging, Jackpot, Bar-Bar-Bar espers), Runic (spells, weapons), Bushido weapons, can't-miss/no-split flags, Umaro odds/powers, Sketch brush rule, the two-handed grip (x1.5, Bushido, Windslash/Aero), Shock (probe-checked), Leap's limits, Mimic not copying a Mimic, one counter per X-Magic, True Knight cover (self-tested), Sketch's Magic for Vigor done; dual wield/Offering, the grip, Shock's HP cost, Mimic, X-Magic, the back-row cover and Sketch untested in a fight; left: BNW's evade tweaks (a covering knight's Evade halved, no Rerise Evade penalty, no status hit bonuses), Interceptor not blocking a covered hit, Umaro tossing Mog as a sure critical (BNW c2.asm: 141 sections) | 25 | 87% | 21.8 | some (probe, vanilla baseline, proc/relic/special/flag porting) |
| Menus and battle UI: Wait Gauge cast bars + ATB colours (v0.11.0, untested), Scan (free turn, no counterattacks; the PR's Scan lists statuses itself), dance moves in the dance descriptions; BNW's other menu hacks fix SNES screens the PR redrew (status/equip screens, MP costs, Blitz names, spell icons); left: the "Fail" / "Null" miss messages, the Rage menu's attacks (no PR field) | 5 | 60% | 3.0 | none |
| Text: names and descriptions done; monster battle lines in compiled AI (45, battle_text); dialogue groundwork (SNES decoder, alignment prototype: 29% of PR lines matched, not used yet); dialogue (BNW rewrote 99%) and the PR-scripted bosses' lines left | 10 | 39% | 3.9 | names/descriptions |
| Graphics: enemy sprites (2026-11-05, port_sprites: 141 same as vanilla; shape swaps Banshee = Critic, Europa = Fortis, Kefka 375 and Kaiser 311 no longer the Guard placeholder; ~220 BNW recolors and 23 new art need image work, the PR's sprites are pre-drawn) | 2 | 15% | 0.3 | shape swaps (Beyond Chaos: 4 in this seed) |
| Testing and release: full playthrough (the game harness plays headless: new game, first battle, the Whelk, event fights by warp + script, a battle log; 2026-11-07), packaging (one zip per mod, tools/package_mod.py) | 3 | 25% | 0.75 | deploy tooling, the harness |
| **Total** | **100** | | **81** | |

Transfer to other mods: of the 81 points done, ~51 are shared code (incl. the formula probe and its vanilla baseline, stat caps, defense flags) (tooling, porter,
AI compiler, generic plugin pieces), so ~75% of the work so far carries over. The
2026-11-06 chest matcher (per PR file, inside WC's chest areas, event-layer chests) is shared
too: BNW and Beyond Chaos get it on their next port, and the Mesen 2 harness of
`test_wc_xref.py` runs any SNES ROM headless. What
that gives each mod today (all three ports run and pass `tests/test_mod.py`,
checked 2026-11-04; none tried in game):

| Mod | Done | What's there / missing |
|---|---:|---|
| Beyond Chaos (CE 6.1.3, seed 9859608359) | ~56% | its randomized data all ports (commands, espers, items, shops, chests, enemies), sprite shuffle as shapes (4 swaps in this seed); missing its custom commands (assembly), weapon procs, palette/music shuffles |
| Worlds Collide (1.4.3) | ~99% (table below) | in-game seeds with all of the website's options, made and applied without a restart, saves keep their seed (and the Deck gets the PC's seeds); randomized data + its AI, scaling, multipliers, boss/dragon/statue shuffles, the Coliseum and auction; WC's start and travel between worlds; all 62 checks with their character gates; the Track menu; objectives with every result and all 15 quests; the character, command, esper, item, chest, challenge and display options except those listed under "Left in WC" below (none in the main preset); the story's dialogue cut as WC does; no achievements; first playtests on the Deck 2026-11-06 (fixed: the WoR-state start, the unique-command crash) |
| T-Edition | ~5% | tooling only; needs a Japanese-ROM layout profile (offsets, text encoding, ExHiROM) before anything ports |

**Worlds Collide, weighted** (2026-11-06; first made 2026-11-05 to replace a running estimate that
had drifted up):

| Area | Weight | Done | Points |
|---|---|---|---|
| Seeds in game: options screen, generator (ROM-free since 2026-11-06: the ROM's data tables only, website-identical seeds), live apply, saves keep their seed (and the Deck gets the PC's seeds and slots: deploy's seed sync) | 15 | 98% | 14.7 |
| Randomized data: characters, commands (MagiTek too), espers, items, shops, chests (all of them: the PR's empty ones, Thamasa's, every monster box, the Phantom Train's pick-ups), enemies, bosses' AI, scaling, multipliers, the Coliseum, boss shuffles, esper bonuses/restrictions, start skills, sprites (the PR's own art; sprites from other games left out by choice) | 30 | 100% | 30.0 |
| Open world: start (party, levels, airship with its wheel, gil/items/espers) and travel between worlds | 4 | 95% | 3.8 |
| Events: WC's 62 checks (27 points, 0.44 each), WC's story flags (4: start state ported and audited in 51 places, ~90%) | 31 | 62 of 62 checks + start state | 30.6 |
| Objectives: exported per seed and tracked (characters, espers, checks, dragons, bosses, all 15 quests); every result | 6 | 100% | 6.0 |
| Track menu (Checks, Progress, Objectives, Flags) and hints (the auction door hint, WC's only one) | 4 | 100% | 4.0 |
| Flags that are assembly patches: done -scan/-warp, -hf, -etr/-etn, -nosaves, -fst, -brl, -rnl, -lel, -dns, -dda, -ems, -eu, -sal, -pd (with its metamorph sets), -nee's metamorph set, -frw/-frm (script flashes), -scis (Sketch and Control), -scia, -csb, -ccrs, -chrm, -llr, -saw, -rc, -ond (the PR's job title shows a renamed or re-skinned character's original name), -nxppd (EXP not split), -fbs (Kefka's Tower's boss skips). Left out by choice (cosmetic, no bearing on play; off the options screen since 2026-11-06): -npctips, the Y NPC (-y*), -ahtc, battle-animation flashes (the PR's effects are its own; -frw/-frm cover story scenes) | 10 | 100% | 10.0 |
| **Total** | **100** | | **~99** |

Left out of WC by choice (2026-11-06, the user's call: cosmetic, no bearing on play; their options
are off the in-game screen, a pasted or preset flag is still passed to WC, so seeds stay the website's):
WC's NPC tips (-npctips: WC's 317 dialog rewrites; 140 matched the PR's lines confidently), the Y NPC
gag, healing text color (-ahtc), battle-animation flashes, and sprites WC takes from other games (only PR art: the 14 characters, Soldier, Leo, Banon,
Ghost, Kefka; the conversion is researched: WC's 307 sheets hold every pose, the PR's field and
battle frames are 32x32 and its portraits 40x40, Magicite takes a .png + .spriteData per frame).
Chests: every SNES chest a seed changes has its PR chest (test_wc_xref, 2026-11-06): the 8 that
start empty (the PR's scripted empty chests, filled per seed), WC's 10 Thamasa ones, all 11
monsters-in-a-box (on their own background), and the Phantom Train's 3 (the PR keeps two as Tent
pick-ups on its luggage racks, filled per seed; the third, dropped beside one, joins it). WC's -chrm boxes (a plain chest made a box: the
plugin starts the battle) fight on their formation's background; neither preset uses -chrm. Some checks' scenes still show lines no character speaks (Phoenix
Cave's "Is that...magicite?", Deathgaze's "A piece of magicite fell...", Lone Wolf's "Snatched
the treasure..."), which WC cuts check by check (2026-11-06: the plugin now skips every line in
a check's own scene but the reward's and a question's; lines in a scene's earlier scripts stay).
BNW's port had the same per-formation battle background bug (fixed in port_encounters; BNW re-ported
2026-11-06). BNW's Veldt needs no hook like WcVeldt: its table is rebuilt (tools/port_veldt.py). Party pick on character rewards (WC's RecruitAndSelectParty): ported 2026-11-06
(the PR's party select screen after each character a check gives).
Check NPCs that look like their reward: 21 of WC's re-skinned SNES NPCs are paired with PR map
entities (tools/wc_npcs.py); the rest keep the PR's art (Doma WoR's Cyans, Figaro WoR's Edgar,
Baren Falls' Gau, Mt. Zozo's cliff Cyan, the Serpent Trench Edgar, the Magitek Factory's and the
Opera's, Zozo's esper, Mt. Kolts' palette, Ebot's Rock, the Imperial Camp's Cyan, Mobliz's other
Terras, Deathgaze's magicite).
Sketch uses the sketcher's stats in the PR already (its act is the sketcher's). The story's
cutscenes play their movement without the skipped lines (WC rewrites most scenes away). WC's rage edits (-rnc, -pd's Rhobite) do port (port_effects.port_rages; an
earlier note here said otherwise). Untested in play: everything, by design (the user's
playthroughs).

Checks are counted as WC's Track menu counts them (62; before batch 7 the unit was 68 reward
slots). Each check is about 0.44 points (27 / 62); objectives are 6 points.

### WC without an SNES ROM (the user's goal, 2026-11-06: our own randomizer at parity with WC)

Until 2026-11-06 the in-game generator ran WC on the vanilla US ROM, which the bundle carried
(not distributable). Findings:
- WC reads 437,822 bytes of its input ROM (170 regions): data tables (characters, items,
  espers, spells, shops, chests, enemies, formations, coliseum), but also dialogue (134 KB),
  character sprites (126 KB), maps/NPCs/exits, enemy AI scripts, names and palettes.
- It needs all of them: with graphics, dialogue, maps or enemy AI blanked it stops (it decodes
  sprite sheets, dialogue, compressed tilemaps and AI scripts to rewrite them), and it only
  accepts the exact vanilla ROM (a SHA-256 check).
- So "ROM-free" means running WC's randomizing logic on data from the PR's own tables, with
  the ROM-only rewriting (sprites, dialogue, maps, AI text) stubbed out while every random
  draw stays in WC's order, so a seed still matches the website's. `test_wc_xref.py` is the
  yardstick: the same seed through WC on the SNES (testing, with a ROM) and through ours.

Started 2026-11-06:
- `tools/wc_trace.py` records a WC run: every random draw (9,000-10,000 per seed, all through
  Python's random) with the line that made it, and every input byte read, by WC module;
  `--diff` finds the first draw two runs disagree on. `tools/wc_outputs.py` compares the tables
  our port reads and wc_run.py's dumps.
- `tools/wc_romfree.py` (wc_run.py with WC_ROMFREE=1): stand-ins for what WC only rewrites
  (dialogue, maps' NPCs/events/exits, enemy AI scripts, party battle scripts, font widths,
  animation flashes, menu sprites; the SHA-256 check off). WC's AI changes draw once (Rizopas'
  timer): the stand-in makes that draw.
- Result: WC run on a ROM that is zeros except 38,966 bytes of numeric data tables (characters,
  items, spells and skills, espers, enemies and their items, formations, packs, zones, chests,
  shops, rages/sketches/controls, the Coliseum, natural magic, metamorph sets, map properties'
  random-battle bits) makes the same draws, the same tables and the same event choices as on
  the real ROM: 4 of 4 flag sets (both presets, 3 seeds, and open world + random bosses +
  permadeath + NPC tips + original names + custom sprites). No graphics, text, maps, AI, code
  or music needed.
- Done (the user chose to ship the data, 2026-11-06): the generator ships `data/wc_base.bin`
  (`tools/wc_basedata.py`: those tables, plus the names WC and the porter use and the enemy AI
  scripts, so WC's AI changes run as they are and port_ai compiles them as before; 94,656 bytes,
  44 KB compressed, made from the ROM on the dev PC). `tools/wc_seed.py` runs ROM-free wherever
  there's no ROM (FF6_WC_ROMFREE=1/0 forces it): WC's input from that file in memory, its
  output compressed in the work folder (`ff6snes.rom_bytes`), never an .sfc; the porter's
  "vanilla" side is the same input. `tools/wc_parity.py` makes a seed both ways and compares the
  folders: the same for every configuration tried. The bundle has no ROM (it refuses to build
  with one).
- Why it holds: WC only draws from Python's random, and none of the stood-in content changes a
  draw or a table the porter reads; WC's code is untouched, so a website seed is our seed. A
  new WC version needs `wc_basedata.py regions` (fresh traces) and `build`, then the parity
  and cross-reference tests.

### WC without the game's files (2026-11-12, the user asked how to distribute WC)

The player zip carried 1,804 of the Pixel Remaster's own files, byte for byte: `generator/vanilla`
(1,345: master tables, messages, monster AI, map entities), `baseline/files` and
`baseline/vanilla_data` (vanilla copies of what a seed changes) and the Magicite folder's vanilla
copies. Same rule as the ROM: they're made on the player's machine from their own game now.
- `tools/pr_bundle.py`: the PR's UnityFS bundles (LZ4/LZMA blocks, SerializedFile v21, AssetBundle
  m_Container, TextAsset) with the standard library alone, because the bundled Python has no
  UnityPy. Byte-identical to `extract.py` (UnityPy) on all 2,374 text bundles (8,173 files, 30 s;
  WC's 273 in 4 s).
- `tools/wc_gamedata.py`: first (morning) `setup` ran the porter on the player's machine: 3,009 game
  files extracted, then the baseline and the cache rebuilt in fresh processes, 35-42 s. Then (the
  user: "all copies of the game have the same game data", "sure, do that") a recipe made once by
  `wc_bundle.py` (`make_recipe`): each of the 1,633 made files is a copy (468), a game monster AI
  script renamed for its slot (384), a line patch of a game file's canonical JSON (one value a line,
  re-saved compact) or text (243 + 25), a duplicate of another (385), or ours, shipped (128: key
  lists, wc_baseline.json, the cache, which setup re-stamps for the player's folder). Setup reads
  1,584 game files and makes everything in memory: 10 s with the bundled Python (5.5 extract, 4.5
  make), every file checked against its sha1; 18 s inside a busy PC's first start. `vcache.stamp`
  now skips dot-files (`.complete` in the vanilla folder had made every cache stale). The recipe is
  4.2 MB (its patches carry WC's rewritten scripts, which the old zip shipped whole).
  `selftest` (this PC: a build without its game files, set up from the game, compared file by
  file), `status`.
- Checked: setup's output = the dev build's for every file (generator/vanilla 1,345, cache 3,
  baseline 643, Magicite folder 959), and a seed made on the set-up install (bundlecheck, Moogle
  Cup) = the project generator's (wc_parity).
- The packager refuses any file byte-identical to one in pr/vanilla (BNW's zip: none but two
  Magicite key lists, which are asset names and exempt), leaves WC's made parts out (modkit.json
  `package_leave_out`, `package_magicite: false`: 12 MB instead of 18), and refuses WC until the
  plugin runs the setup (`player_setup`). The plugin does: `WcGameData.EnsureSetUp` first thing in
  `Plugin.Load` (WC loads before Magicite, so no restart), checked headless from a removed
  `.complete` (recipe setup: 18 s in Load, startup apply ok; the next starts skip it, 0 failures,
  534/534 Magicite files).

### WC startup time (2026-11-12, the user: "the startup is incredibly slow", "the command prompt before the game is the thing taking forever")

Measured headless on the PC by timestamping the BepInEx log as it's written (a watcher; the log
has no times). Before: 15.0 s of console (BepInEx preloader 2.4, Il2CppInterop 2.8, WC's Load 6.6,
Syldra 0.6, Magicite 2.5) and 32.5 s to the title, 12.8 s of it the opening logos after the data
had loaded.
- Logos (`WcBoot.cs`): SceneSplash (decompiled: `tools/ghidra` patterns for SceneSplash and
  Last.UI.SplashController) goes to the title when Update sees loadFinished, messageIsReady and
  animFinished; OnSplashAnimationFinished sets animFinished and requests the title scene, called by
  the logos' controller after the last logo. A press can't end them early (each logo's own isSkip,
  and SplashController.IsInput is inlined into the logo loop, so hooking it does nothing). A
  SceneSplash.Update prefix calls OnSplashAnimationFinished once the game has loaded; a prefix on it
  drops the logos' own later call. **Removed the same day** (the user: "remove the skip logo stuff, it
  doesn't really do anything"): the logos play again; don't bring the skip back.
- WC's Load (`[wc] load:` line, per-step ms): 6.06 s warm, of which the screen's setup 1.25 s (its
  first touch loads Unity's GUI types), the first hook 0.7 s (Harmony's IL2CPP setup), seed hooks
  0.75, registering WcRunner 0.6, test request 0.6, the gameplay hooks ~15-130 ms per group. The
  screen's setup and every gameplay hook group now run one a frame from WcRunner's first Update
  (`[wc] first frames:` line, 2.2 s over 24 frames); Update and OnGUI wait for them. Load keeps
  config, game data, flags, the logo skip, input, seeds (Init + save hooks), tests and the runner:
  2.96 s.
- Result, warm PC start: console 15.0 -> 10.3 s, title 32.5 -> 19.5 s (with the logo skip, since
  removed: the console's 10.3 s is the deferral's alone). The first start after a
  deploy re-copies the seed's 534 Magicite files (1-4 s more), players don't redeploy. Left:
  BepInEx and Il2CppInterop (~4 s), Magicite and Syldra (~3.4 s), Harmony's setup and WcRunner's
  registration (~1.3 s). The Deck isn't measured yet.

## Modding kit (any Pixel Remaster; 2026-11-11)

`pr/MODDING.md` is the guide. `tools/kit.py` (package `tools/prkit`) knows all six PRs through Steam's
library list and does extract / api / setup (BepInEx + Magicite into another PR) / new-mod / check /
package / deploy (PC + Deck) / run (headless) for any mod in `pr/modkit.json`. `plugin/PrKit` is the
plugin library new mods compile in (`PrKit.props`): the hooking rules below as code (`Hooks`),
switchable `Feature`s, `Master`/`GameText`, `TestRun` (no saves, muted) and `Harness` (BNW's harness for
every PR), `LiveData`, `SingleGamepad`. BNW's plugin is on it since 2026-11-11 (v0.22.1); WC's waits for
the WC session to free WcPlugin (MODDING.md, "Where BNW and WC stand"). `tools/package_mod.py` now reads
the manifest (the same zips, checked file by file).

## Shared porter

`tools/port.py <profile>` diffs a modded US-v1.0-layout ROM against vanilla and
writes `mods/<ModName>/` (Magicite). All converters are mod-agnostic; a profile
(`tools/profiles.py`) only sets the ROM, output name, text policy and extras:

| Profile | ROM | Text | Extras |
|---|---|---|---|
| `bnw` | build/brave-new-world/…FRC.sfc | BNW names everywhere + BNW descriptions (own encoding) | `bnw_data.json` (esper rules); Rage odds 2/3 : 1/3 |
| `beyond-chaos` | latest build/beyond-chaos/*.sfc | only names/descriptions the seed changed | — |
| `worlds-collide` | latest build/worlds-collide/*.sfc | same | — (events not portable as data) |
| `--rom X --name Y` | any US v1.0-based mod | same | — |

`port_bnw.py` is a wrapper for `port.py bnw`. Test any port with
`PR_MOD=<ModName> pr/.venv/Scripts/python -m unittest discover -s pr/tests`.
Only one ported mod should be installed in the game's Magicite folder at a time.

Beyond Chaos (New Player seed 9859608359) ports with no BC-specific code:
characters/commands/names, equipment (98 new equip groups), 348 enemies,
espers, shops, 193 chests, 151 encounter sets. Not portable as data: BC's
custom commands, sprite/palette/music shuffles, weapon procs.
T-Edition needs a Japanese-ROM layout profile (different table offsets, JP text
encoding, 6 MB ExHiROM relocations) — not started.

## Setup (done 2026-11-04)

- Game: `D:\SteamLibrary\steamapps\common\FINAL FANTASY VI PR`
- BepInEx 6.0.0-pre.2 (IL2CPP x64) + Magicite 2.2.0 installed; added files are
  listed in `pr-install-files.txt` (delete them to uninstall).
- Magicite mods go in `FINAL FANTASY VI_Data/StreamingAssets/Magicite/<mod>/`.
- `pr/.venv` — Python 3.12 venv with UnityPy (`.venv/Scripts/python`).
- `tools/extract.py` — dumps every text asset from the game's bundles into
  `vanilla/<group>/<container path>` with `keys/Export.json` (Magicite layout).
  8,173 assets, 141 MB, audio skipped unless `--audio`.

## Sources of truth

- BNW ROM: `build/brave-new-world/Brave New World 3.0 FRC.sfc` vs vanilla
  `rom/vanilla/Final Fantasy III (USA).sfc` (US v1.0).
- BNW source: `mods/brave-new-world/source` (git clone of Gens81/brave-new-world,
  `asm/banks/*.asm` = every code change, commented).
- BNW manual: `mods/brave-new-world/3.0-final-rc/.../Printme 3.0.pdf`,
  `full_changelog.txt` (3.0 delta only).
- SNES table layouts: Worlds Collide's `mods/worlds-collide/data/*.py`.

## How much BNW changes (bytes differing from vanilla v1.0)

Enemy AI 95%, dialogue 99%, espers 82%, level-up growth 98%, spell names 91%,
item descriptions 95%, enemy stats 59%, formations 56%, shops 46%, spells 39%,
character init 34%, items 25%. Code: ~75 KB changed/added (battle C2 23%,
menus C3 17%, new code in free space).

## PR data model (vanilla/master/.../Master/*.csv, 96 tables)

| SNES table | PR table(s) |
|---|---|
| Character init + level growth | `character_status`, `growth_curve`, `exp_table` |
| Items (one 256 list) | `item` (consumables), `weapon`, `armor` (armor/shield/helm/relic), `content` (names/icons for everything) |
| Spells / skills | `ability`, `ability_group`, `command`, `condition_group`, `attribute_group` |
| Espers + spell learning | `learning`, `ability` (summons), `content` |
| Equip permissions | `job_group` (character combos) via `equip_job_group_id` |
| Enemies | `monster`, `monster_asset`, `monster_flag_group` |
| Enemy AI | `monster_ai/.../MonsterAI/sc_ai_*.json` (72 mnemonics: StartTable/Act/CounterAct/Is*...) |
| Formations / packs | `monster_party`, `monster_set`, `encount_*` |
| Shops | `product`, `product_group` |
| Chests | `treasure_box` + map entity JSON |
| Text | `message/.../Message/system_en.txt` (names, descriptions), `story_mes_en.txt` (dialogue); TSV `KEY\tvalue` |

Stat names in PR: strength=Vigor, agility=Speed, vitality=Stamina(BNW "Spirit"), magic=Magic.

Magicite partial CSVs: header row + only the edited rows (full rows).
Partial TXT: only edited lines.

## Verified mappings (tools/idmap.py, checked against PR vanilla)

- Characters: PR `character_status` id = SNES id + 1 (stats identical).
- Items: weapons 0-89 → `weapon` +2; shields/helms 90-131 → `armor` −85; body
  armor 132-162 → `armor` −84 (PR armor 47 unused); relics 176-230 → `armor` −97;
  tools 163-170 → `item` 30-37; skeans 171-175 → `item` 25-29; consumables
  232-254 → `item` 1-23 (GBA names: Tonic→Potion…); Rename Card 231 → `item` 24.
  254/255 match on stats; stat boosts match 100%.
- Enemies: `monster` id = SNES id + 1 (372/384 identical; rest are dummies).
  Bat.Pwr→`attack`, magic→`ability_attack`. Steal: slot1 = common, slot2 = rare.
  Drops: common only → rate 87, rare only → 12, both → 100 (d1 common, d2 rare).
- Equip permissions: SNES char bit b → PR job b+1, plus jobs 15-18 (guests) for any
  wearable item and job 19 when Terra (job 1) can equip.
- Spells: `idmap.spell()`: black/gray +40, white −14, espers by table, Bushido/Blitz
  +39, dance moves +71, Magitek +87, Lores +1, enemy skills +137, desperation −12.
  241/251 match on MP+power (rest are PR tweaks/removed duplicates).
- Ability fields (learned by tools/learn.py): mp→use_value, power→standard_value,
  accuracy→accuracy_rate, elements→attribute_id/attribute_group_id,
  targets→renge_id/battle_renge_id. Status effects live in
  `battle_function_group_id` → `function_group` (function 9 = add status, value1 =
  condition id; function 3 = damage).
- Learning (`learning.csv`): type 1 = level-up (job = character), type 2 = esper
  spells (value1 = esper content id 61+n, value2 = rate), type 3 = Mog dances,
  type 4 = equipment-taught spells (value1 = equipment content id).
- Growth: `growth_curve` group 1, `hp_value1`/`mp_value1` per level; SNES HP table at
  0x26F4A0 matches PR exactly; PR rebalanced MP (BNW overrides it anyway).

## BNW specifics found

- BNW reassigns spell slots (e.g. slot 22 = Demi). Port spells together with
  their names/descriptions, not numbers alone.
- BNW name tables moved: magic names 0x2FFC00 (8 bytes × 54), esper names
  0x2FFDB0 (9 × 27), skill names 0x26F567 (11 × 175). Item names stay at 0x12B300
  (new icon bytes ≥ 0xEB).
- BNW turned relics 188/189/208/209 (Cherub Down, Cure Ring, Gauntlet, Genji Glove)
  into consumables: needs new PR item rows.
- Esper equip restrictions: `EsperData` table in `source/asm/banks/c3.asm`, 4 bytes
  per character (bit = esper id, SNES order).
- `source/bnw 3.0/scripts/`: `dialog_script.txt` (full BNW dialogue),
  `monstBattleScripts.txt` (all AI as annotated text, 2.1-era reference).

## Spell port design (tools/port_spells.py)

Slot-based: PR ability `idmap.spell(i)` becomes BNW spell i (all BNW references
use slot numbers). Each slot borrows presentation (animation, school, icon, menu
SE, base effect functions) from a *template*: the vanilla spell with BNW's name
(or `ALIASES`), read from an untouched copy of vanilla. Numbers come from BNW via
learned value tables; statuses are rebuilt as function 9 (one status), 10 (status
group) or 12 (removal group, flags3 & 0x04) only when BNW's statuses differ.
Text: names from BNW's relocated tables (Bushido at 0x0F3C40), descriptions
decoded from the BNW ROM's own pointer tables (`tools/bnw_text.py`; the repo asm
is slightly ahead of the 3.0 build, so data always comes from the ROM).
Keys shared with items (`MSG_ITEM_*`, e.g. scrolls) are left to the item pass.

`mods/BraveNewWorld/bnw_data.json`: per-esper SP costs, EL bonus (text + id),
equip bonus text and which characters may equip it, for the plugin.

## Resistances (tools/port_resist.py, tools/groups.py)

Elements: SNES bit → PR attribute [1 fire,2 ice,3 bolt,6 poison/dark,8 wind,7 holy,
4 earth,5 water]; type 4 absorb, 3 null, 2 half, 1 weak (enemies bytes 23-25;
armor half 15, absorb 22, null 23, weak 24; weapon element byte 15). Status
protection bytes 6-7, auto-status byte 8, enemy immunity bytes 20-22, enemy
initial statuses bytes 27-29 (byte 30 = flags). Groups are only rebuilt where BNW
differs from vanilla. Element map rebuilds all 373 vanilla enemies exactly.

## Encounters (tools/port_encounters.py)

PR re-zoned the world maps: areas 164-188 (WoB) / 264-297 (WoR) each use one
`monster_set` (401-425 / 451-484) equal to one SNES pack; dungeons use
`map.monster_set_id`; sets are 4 parties at 80/80/80/16. Sets are matched to
vanilla packs by enemy line-ups (184/188). For each set: SNES places that used
that pack (world zone/terrain slots 0x0F5400/0x0F5500, dungeon maps 0x0F5600
with random battles on: map props 0x2D8F00 byte 5 bit 7) → majority BNW pack
there → set refilled with new parties 2000+formation (vanilla parties untouched
so event battles keep theirs). Layouts: copied from a vanilla party with the
same line-up, else nearest free cell of the 3×3 grid (cells 1-3 front, 4-6
middle, 7-9 back; top/middle/bottom). Packs that are all empty formations are
left alone. Not ported yet: encounter rates; 3 maps where BNW turned random
battles off (needs SNES map ↔ PR map mapping).

## Equipment effects, procs, Rages (tools/port_effects.py, port_procs.py)

Source of truth for the bits: a research pass over BNW's asm (bnw 3.0/asm overrides
asm/banks where they differ; e.g. RandomCast is item.asm:310). Item bytes 5/9/10/11/12/13
-> battle RAM $11DF/$11D5-$11D9, $3C58/$3C59 = bytes 12/13, $3CBC = byte 27.

Relics/armor: `wear_function_group_id` functions 60 HP/MP % (value1 1 HP, 2 MP), 61
command change (from, to), 62 add equip ability (HP+25/50/12.5% = 286/288/245, MP =
287/289/244, Gauntlet 251, Genji 252, Offering 253, MBlock +10..+50 on weapons 256-259),
67 MBlock, 75 MP cost; learned per SNES bit from vanilla (`G.learn_bits`). Cover,
counter, always-hit and SOS live in **`wear_condition_group_id`** instead (conditions
29/48/49/46/47; `CONDITION_BITS`, checked against vanilla armor), built as multi-row
condition groups for BNW's combinations (Leo's Crest = cover + counter + always-hit).
BNW puts relic bits on **weapons** too (HP%, counter, cover, two-hand, dual wield,
Offering, proc rate): applied there as well. BNW-only bit meanings come from the profile
(`relic_bit_funcs`: 10.4 Leo's Crest = Runic -> Shock, 61(16,28)).

Weapons (rebuilt from vanilla's own data: 90/90 weapons for functions, species, evade;
89/90 for program and on-hit condition): byte 26 = MBlock (high nibble, function 62
256-259) + evade/10 (`evasion_rate`); byte 27 high nibble = special effect (low nibble is
only the parry graphic): 1 steal THIEFS_KNIFE, 3 X-Kill ASSASIN_WEAPON + condition group
100, 4 x2 vs humans (`effective_species` 2), 5 drain fg 13, 6 MP drain 77+13, 8 HAWK_EYE,
9 DICE, B Tempest (change_ability_by_weapon), D SWORD_THAT_CUTS_IRON (1/3/4/8/D also
function 23). 2 Atma, 7 MP crit, A Valiant, C heal, E Ogre Nix are id lists in code.
BNW's meanings (`profiles.BraveNewWorld.weapon_specials`): 3 = x2 humans + 50% Net, 5 =
x2 humans + ignore Defense, 6 = 50% Net, B = Windslash, E = 50% Aero replacing the swing,
F = undead slayer (plugin todo). Conditions no SNES bit explains (the PR's condition 64
on Guardian/SwordBreaker/Falchion/Murasame) stay only where BNW kept the same weapon.

Weapon spell procs (`tools/port_procs.py`): the spell is data after all, in the
`battleextradata` group (`battle_extra_data.json`): `add_ability_weapon`
{weapon_id, ability_id, ability_per} (vanilla 18 entries, 25%); `process_prog =
ADD_MAGIC` only marks the weapon. ability_id is a *proc copy* of the spell (vanilla
263-276: the spell's row with type 629, invalid_reflection 1, ability_wait 0,
content_flag 0, nameless content row). SNES byte 18 bit 6 = cast randomly, bits 0-5 =
spell; vanilla data and ROM agree on all 18. BNW: 25 proc weapons (12 new, 7 other
spell, 5 removed), 11 new proc copies (ids 5918+), the 13 vanilla copies carry BNW's
changes to their spell. The file is written whole (Magicite merges JSON by top-level
key, `MergeJsonDictsInPath`). The plugin (`WeaponProcs.cs`) sets every entry's
ability_per to the acting character's chance before each action is built
(`FunctionProvider.CreateActFunction` prefix; BNW: 1 in 4, 1 in 2 with "High Proc
Rate"). Odds (bnw 3.0 item.asm RandomCast, `profiles.proc_odds`): 1 in 4; "Use Brush
Odds" (byte 11 bit 2: Soul Sabre, brushes) 1 in 2; "High Proc Rate" (byte 12 bit 7, on
5 relics and 6 weapons) 1 in 2, or 3 in 4 with brush odds. Weapons' own odds are data
(ability_per); the relic part is the plugin (bnw_data.json procs.weapons rate/high_rate).
Special-effect procs (`special_procs`): 50% Net on Kusarigama and both Kagenui;
change_ability_by_weapon Kazekiri -> Windslash 25%, Mutsunokami/Windbreaker -> Aero 50%.
Relm's brushes need no retargeting: their attack heals/targets allies (NormalAttackHeal
list), so their Safe/Shell/Reflect/Haste procs land on the ally. A mod that clears "cast
randomly" on most vanilla proc weapons but keeps their spells (Beyond Chaos) has its own
proc rules: vanilla procs and rods are kept for it.

Items the PR keys effects on in code (`HardItems.cs`, bnw_data.json hard_items): static
members such as SpcialFlagData.EncountDownItemId/NonEncountItemId/HealingItemId,
CalcExecuteCorrection.Physical/MagicCorrectionAccessoryIds (dict of arrays),
UseMpCriticalWeaponIds, NormalAttackHeal, IgnoreDefenseItemIds, bloodSwordId,
BattleActExectionSerial.UseBrokenWeaponIds, SerialBattleUtility.ArmorIdHornOfDragon,
ParameterProvider.RingsOfTheDeadId, PassiveContentProvider.ContentId* — all static,
settable. BNW reused those slots (Moogle Charm would block encounters, Daryl's Soul double
EXP, Black Belt halve encounters), so each list is rewritten to BNW's items, but only if it
holds exactly the expected vanilla items (as item ids or content ids, whichever it uses);
re-checked every 300 frames until settled (a late static constructor would undo it).
x2 EXP/gil: PassiveContentProvider.GetExp/GetGil postfix returns the base value (BNW has
neither). DashItemId is left alone (BNW: everyone dashes; turning it off might stop dashing).

**Two IL2CPP traps (both shipped once, both fixed):**
- v0.11.0 crashed the game (AccessViolation, uncatchable) writing
  `SpcialFlagData.EncountDownItemId`: it is a C# *constant* (literal field, no storage);
  Il2CppInterop still exposes a settable static property. Before any static write the
  plugin now reads the field's flags (`HardItems.WriteProblem`:
  `NativeFieldInfoPtr_<name>` -> `IL2CPP.il2cpp_field_get_flags`, FIELD_ATTRIBUTE_LITERAL
  0x40) and never writes constants. Constants found: SpcialFlagData.EncountDownItemId /
  NonEncountItemId / HealingItemId, Tamanosuzu.HealingItemId,
  SerialBattleUtility.ArmorIdHornOfDragon, PassiveContentProvider.ContentIdGaleHairpin /
  ContentIdAlarmPiercing, BattlePassiveConstants.BlackBeltCounterPercentage (75).
  Real static fields (rewritten fine): CalcExecuteCorrection lists, bloodSwordId,
  BattleActExectionSerial.UseBrokenWeaponIds.
- v0.11.2 patched SpcialFlagData.IsEncount/GetEncountValue/IsMoveHealing, which take the
  party as `ref List<OwnedCharacterData>`: Il2CppInterop's Harmony support emits invalid
  IL for a by-reference *object* parameter (InvalidProgramException on every call: ~200
  per minute of walking, encounter checks failing). Don't patch such methods. Hooks with
  `ref int` parameters (IsUseMpCriticalOccurrence, GetAccessoryMagicCorrectionValue) have
  not thrown. Still todo: the encounter relics (Moogle Charm slot still blocks
  encounters, Charm Bangle slot halves them) — candidate hook points are the lambdas
  `SpcialFlagData.__c._IsEncount_b__20_0` / `_GetEncountValue_b__22_0`
  (KeyValuePair<EquipSlotType, OwnedItemData> by value), untested. Walking regen is
  cancelled at `Last.Map.Tamanosuzu.CheckUseItem` (no by-ref parameter). Dragon Horn is
  redone in `CanContiniusJumpAttack` on BNW's items (Dragon Helm).
- Physical correction dictionary (`CalcExecuteCorrection.PhysicalCorrectionAccessoryIds`,
  key = PhysicalType): 0 PilgrimsPassport = Offering [armor 114], 1 GenchisBasho = Genji
  Glove [112], 2 BasketsOfGiants = Atlas Armlet/Hero Ring (rewritten to BNW's +25%
  relics), 3 Gauntlet [111]. 0/1/3 key on vanilla relic slots that are dead in BNW, whose
  abilities BNW put on weapons (62(253/252/251)): so Offering weapons/Daryl's Soul likely
  get 4 hits without the damage cut and two-hand/dual wield get no correction. Todo:
  postfix GetAccessoryPhysicalCorrectionValue for 0/1/3 by the wearer's equipment
  abilities, with BNW's values (Offering 2 hits at -25%, dual wield -25%, two-hand x1.5)
  once the value format (multiplier vs fraction) is seen in a log.
  EquipmentAbilityData.TwoHandedMagnification is 2, not 175: it doubles the attack (2026-11-06:
  the grip, below; RelicRules applies BNW's x1.5).
Same porter, weapon usage bits (checked equal on every vanilla weapon): byte 0 bit 4
"throwable" = `throw_flag` (BNW makes Throw a property of a chosen few weapons: the
manual's "Throwable" tag; ~50 weapons lose it) and byte 18 bit 7 "break it from the
Item menu to cast" = `destroy_rate` 100 (BNW: only the Fire/Ice/Thunder Rods;
Windbreaker, Doomstick, Quartrstaff are plain staves).
Rages: SNES 0x0F4600 (2 attacks per monster) → the monster's
`rage_ability_random_group_id` rows (BNW odds 67/33).

## Battle engine (BNW c2.asm; research 2026-11-04)

### What the PR itself does (web research + PR data, 2026-11-05)

Nobody has published the PR's formulas (no datamine or decompile found; GameFAQs and the
Fandom wiki block automated reads, TCRF served bait pages). What is known, with the
source and what it means for BNW:

- Formulas follow the SNES version (Game8: "基本的な仕様はSFC版に準拠"); our 3 logged fights
  agree. Damage cap 9999 (`game_constant_int.csv` MAX_HIT_VALUE).
- **Stat caps 128**: `game_constant_int.csv` MAX_STRNGTH / MAX_VITALITY / MAX_AGILITY /
  MAX_MAGIC / MAX_ABILITY_ATTACK = 128 (Attack, Defense, MDef, evasion 255). BNW caps at
  255 (c2.asm:780 GengiCheckFunction; EL bonuses alone stop at 128, AddEL). Checked
  2026-11-05: BNW characters never pass 128 on natural stats + gear (the part the PR sums;
  the plugin adds EL and esper bonuses after, up to 255); with every EL and the best gear
  in one stat only Locke's Speed (130) and Strago's Magic (134) pass it. Monsters do: four
  scripted ones (Typhon, Ymir, Siegfried, #380; HP 1) have Speed or magic power 255
  (vanilla PR monsters: Speed <= 99, magic power <= 55; the PR keeps monster Speed in
  `agility`, magic power in `ability_attack`). **Done**: `port.py stat_caps` writes
  game_constant_int.csv with the caps at the profile's `stat_cap` (BNW 255) and raises a
  cap to 255 by itself where a ported monster passes it (Beyond Chaos: MAX_AGILITY; Worlds
  Collide: none). The probe shows Vigor past 128 adds nothing to Fight (Vig2 caps at 255),
  as on the SNES.
- **Evade works** in the PR (several player reports; GBA fixed it, PR follows): Evade
  for physical, MBlock for magic, Blind works. BNW uses the same fix (c2.asm:1465 "Terii
  Senshi evasion bugfix") plus: Image clears 1/3 per dodge (done via condition data),
  no evasion penalty from Rerise, no status evasion bonuses, Blind (BlindHelp), evasion
  halved while covering. So H1 is mostly the PR's own; those extras are todo.
- **Rage**: vanilla PR odds are data, `monster.rage_ability_random_group_id` ->
  `ability_random_group.invocation_rate` 25/75 (195 monsters; 59 have 25/38/37), i.e.
  the PR picks the special 75% (SNES 50%). The porter already writes BNW's 67/33
  (c2.asm:2482). v1.2.1: Rage specials ignore Genji Glove/Offering (Fandom).
- **Steal**: PR is the SNES formula, (50 + Lv - target Lv)/128, Sneak Ring x2 (Steam), rare
  slot 12% (`MONSTER_STEAL_RATE_1/2` 88/12; drops the same). BNW (c2.asm:2495): success
  (Speed x2 + 112)/256, sure at Speed >= 72; rare slot Speed/256. Needs a hook.
- **Genji Glove**: PR dual wield deals -25% (jinsoku.net), the same as BNW's dual-wield
  penalty, so it must not be applied twice. Check the GenchisBasho correction value in
  the [relics] log once someone dual-wields.
- Patch 1.0.5 (player.one): in a pincer every character counts as front row; Sketch
  no longer uses MP; Rage can't inflict a status the target's equipment blocks; Quick and
  Trance can't be used while confused; killing with a weapon's instant death no longer
  gives extra rewards.
- **Bushido** charges in the background (the party keeps acting) and has no gauge wait;
  **Blitz** is picked from a list, then its input is shown (Game8, Steam). BNW's Bushido
  and Blitz changes must fit these menus.
- Dragon Horn's extra jumps don't ignore row (Fandom); Trance points aren't used up when
  a battle ends in Trance; Vanish + Doom no longer works, Vanish on a vanished ally
  misses (Game8); crits reportedly also hit defense-ignoring attacks, Throw and X-Fight
  (GameFAQs board, unverified); Throw reportedly ignores MDef (Reddit, unverified).
- Other battle numbers in `game_constant_int.csv` (data, so portable): encounter types
  of 100: normal 68, back 13, pincer 3, side 3, preemptive 13 (by walk/dash and the
  encounter-rate relic); starting ATB % ranges per encounter type
  (ACCUMULATION_ATB_*, e.g. normal 50-80, preemptive 100); Near Fatal below 1/8 HP;
  escape 50% / 20%, escape cool time 340; enemies target each slot 25%;
  ATB_TOTAL_AMOUNT 550, ATP_COEFFICIENT 21 / enemy 12 (meaning unknown: compare with the
  [formulas] ATB log).
- Not found anywhere: Haste/Slow multipliers, ATB formula, magic evasion scale (255 vs
  128 disputed), Dance stumble odds, Offering details, monster magic x1.5.

A research pass mapped BNW's engine changes to PR hook points (asm refs: banks/ and
bnw 3.0/asm/, which wins). Status: [x] done (untested in game unless noted), [ ] todo.

- [x] Monster special attacks (`tools/port_specials.py`): SNES enemy byte 31 decoded
  (vanilla C2/3302, BNW keeps the bits): bit 7 unblockable (content_flag_group 1), bit 6
  no damage (system 5), 0x00-0x1F status n (function 9, value1 = PR **condition id**, not
  a group), 0x20-0x2F damage x(1+(n-0x1F)/2), 0x30/0x31 drain HP/MP (13, 77). Damage = 2
  (physical) + 23 (special). The PR's own specials are hand-made (41% follow the SNES
  byte), so only the 289 BNW changed are rebuilt (257 rows changed, 279 names). The PR
  applies vanilla's multiplier itself, keyed on the monster (logged: BNW's Phalanx, x3,
  hit for vanilla's x2), so the 185 BNW/vanilla **ratios** are exported
  (bnw_data.json spirit.special_multipliers) for the damage hook.
- [x] Spirit-based abilities (spell byte 7 bit 2, c2.asm:1448 StamTest; 33 + proc
  copies): `BattleFormulas.cs` marks CalcControllerProvider.Get{Magic,Recovery,
  FixedBaseValue*}Status when `.ability` is listed, and PlayerCharacterParameter
  .ConfirmedMagic answers ConfirmedVitality meanwhile (logged once). Monsters (v0.14.0):
  EnemyCharacterParameter.ConfirmedMagic answers BNW's monster Stamina (max HP high byte
  + 1, at most 32; c2.asm:2160), x2/3 for the game's monster x1.5 (below).
- [x] ATB speed (c2.asm:397): BattleProgressATBFF6.ATBCalc x BNW/vanilla: characters
  75(Spd+51) vs 96(Spd+20), monsters x1.171875. Logged per speed band to confirm the PR's
  formula is (Spd+20)-shaped.
- [x] Status data (`profiles.condition_tuning`, condition.csv): Haste/Slow rate_value1
  50 -> 20 (BNW x1.2/x0.8; the column reads as percent: Regen 5), Image until_value 25 ->
  33 (1/3 break per dodge), Sleep/Stop 30 -> 15 (BNW 9 vs 18 ticks).
- [x] Damage log (`DamageLog.cs`, v0.12.0): every Fight/magic/heal result with its
  inputs -> BepInEx/bnw_damage.csv (v0.13.0 adds game_value, bnw_factor; an older file
  with other columns is renamed aside). 3 logged fights (2026-11-05) showed the PR runs
  **vanilla FF6's formulas**: player physical BP + Lv^2(BP+2Vig)/256 x3/2 (BP = the PR's
  ConfirmedAttack), monster physical Lv^2(4 Attack + Vigor)/256, magic SP x4 + Lv Mag
  SP/32, x(255-Def)/256+1, variance 224-255/256, Shell/Safe x170/256, back row x0.5.
- [x] Damage hook (`BattleFormulas.cs` v0.13.0, config [Battle] DamageRules): postfix on
  CalcControllerProvider.GetFightStatus/GetMagicStatus, Hit/Critical results only, the
  PR's value x factor (1..9999): special-attack ratio; D1 characters' Fight/Jump (ability
  type 4/15/16) BNW BP + 2Vig + BP Lv^2 Vig/6144 over vanilla; D5 hits on characters
  ((255-S) - rand(0..30-S/4))/225 over vanilla's mean 0.9355 (S = Vigor physical, Spirit
  magic; the PR's own 224-255 roll stays, so the spread is a little wider than BNW's);
  D6 back-row characters x0.75 (x1.5 on the PR's x0.5); Defend halves magic. Each rule
  logs "[formulas] ..." once. v0.14.0: exact SNES integer steps for BNW Fight
  ((BP/16)*(Lv*(Lv*Vig/16))/24 + BP + Vig2) and the variance past S = 120; back row read
  from the provider's targetCorpsId (the unit's own row; CalcInfo's is ignored by the game).
  Todo: player-on-player x0.25, Morph, D8 Imp halves.
- [x] D3 monster magic x1.5 (v0.14.0): the formula probe shows the PR multiplies a monster
  caster's **Magic** (not spell power) by 1.5 in magic damage and heals, as vanilla (monster
  Cure with Magic 1 heals 48-54 = Magic x1.5 floored; spell power x1.5 would be 65-74).
  BNW removed it (c2.asm:2169). While GetMagicStatus/GetRecoveryStatus run for a monster
  caster, EnemyCharacterParameter.ConfirmedMagic answers 2/3 so the game's x1.5 lands on
  BNW's value and the 9999 cap comes after (a flat x2/3 on the result undershot capped and
  low-Magic spells).
- [x] Ignore-defense flags (`tools/port_flags.py`, v0.14.0 data): the PR's content flag 5 is
  "ignore defense" (every ability the probe saw ignore Def/MDef had it, no other did; flag
  1 = can't miss, 2 = no split). The porter sets it from the mod's spell byte 2 bit 5:
  profile.defense_flags "mod" (BNW: every spell; 49 set incl. Holy, Maduin, heals; 9
  cleared: Meteor, Merton, Trifecta, Rocks...) or "changed" (default: only bits the mod
  changed). Probe run 5: BNW 532 pass, 6 fail. The "physical" bit (byte 2 bit 0: Def
  instead of MDef; 13 BNW spells incl. Rock 311, Shrapnel 352) has no PR flag (the probe
  saw no magic function meet Defense), so the plugin (v0.15.0) scales those spells by
  (255-Def)/(255-MDef) and uses Vigor for BNW's variance (bnw_data
  spirit.physical_abilities); GrandTrain (Lore) and
  TekMissile (Magitek) still ignore defense without flag 5 (another rule for those types);
  a hit the game capped at 9999 keeps 9999 under BNW's variance, so a 128-Stamina target
  takes 9999 where BNW gives ~7000 (Bum Rush, X-Meteo); Meltdown 1350/1524 ~8x.
- [x] Monster magic residual: the game multiplies the answered Magic by 1.5 as a fraction,
  so the plugin also scales by (4SP + Lv*want*SP/32) / (4SP + Lv*1.5*answer*SP/32), SP from
  the ability (the game passes standardValue 0 for spells).
- Probe findings, the PR vs SNES vanilla (pr/probe_report.md, the baseline for every port):
  Fight, monster Fight, magic, heals and Spirit-free spells follow the SNES formulas;
  character Vigor caps at 128 (Vig2 255); the game caches a character's Battle Power for
  its Fight formula (the probe can't vary it). Defense met differs from the vanilla ROM's
  spell flags for Quake, Quasar, Rock (220, 311), Meltdown (ignore MDef in the PR) and
  Aurabolt, Raze, !Aero, Aqua Slash, Avalanche, Shrapnel (meet MDef in the PR; the ROM
  says ignore). The PR keeps these in its own data (content_flag_group: flag 5 on
  Flare/Meteor/Ultima/Merton...; Quake ignores MDef without it), so BNW's flag changes
  (e.g. Holy ignores defense in BNW) are **not ported yet**. Meltdown 1350/1524 hit ~8x
  the formula. Go Fish heals a random 0-303.
- [~] H1 Evade for physical / MBlock for magic: the PR has it (reported, see above).
  Todo: BNW's extras (Blind, no Rerise penalty, no status bonuses, cover halves Evade).
- [x] H2 Spirit evasion of statuses (`CommandRules.cs`, v0.15.0): StamEvdChk C2/23B2,
  evade if Stamina >= random(128) = (Stamina+1)/128; BNW's 61 Stamina-evadable spells
  (byte 4 bit 4) and 149 monster specials with a status and no "can't dodge" bit
  (bnw_data spirit.special_evade_abilities). Postfix on Serial.FF6.Battle
  .AddConditionFunctionSerial.CheckForceMissCondition(attacker, target, condition) ->
  true = that status misses, the ability read from the current CalcControllerProvider
  (only when its attack/target are the same units). Monster Stamina = max HP high byte
  + 1, <= 32. Damage still lands (BNW: an evade only cancels the status).
- [x] S1 Regen/Poison/Sap (`StatusTicks.cs`, v0.15.0; BNW 3.0 tick-calc.asm, c0.asm
  TickLogic): Regen MaxHP/64 + Stamina*Lv/16, Poison/Sap MaxHP/(Stamina/8 + 16), +2,
  <= 255, variance (BNW's on characters taking damage, vanilla otherwise); Poison counter
  c (x(1+c/2)): characters 0,2,4..31, monsters 0,2,5,11,23,31 (x16.5). Decompiled game:
  BattleConditionFunction.GetRateValue(typeIndex 1 Poison / 4 Sap) postfix;
  RegenerationConditionFunction.GenerateRegeneHeal prefix sets healValue (0xD4); tick
  rate x2 by halving BattleUtility.CalcATBProgressCycleForVital(unit, 1/2) and
  CalcGenerateRegeneCycle (110 - Stamina). Elements still apply after (the game's own).
- [x] Reflect (v0.15.0): permanent (condition 24 until_type 7/60 -> 2, profile
  condition_tuning); TargetFunction.OverrideToReflectAct(act, reflectUser) postfix rolls
  85/256 and removes Reflect when the act ends (BattleConditionController
  .InterruptRemoveCondition(unit, ConditionType.Reflex)), except equipment Reflect
  (GetEquipmentConfirmedConditionList) and monsters (innate, kept).
- [x] Turns (v0.15.0): BattleProgressATB.ActExectionEndDelegate (zeroes the gauge after an
  act; FF6's subclass doesn't override it) postfix: Defend (command 2) / Row (5) ->
  gauge 126/256 of MaxATBGauge (c1.asm HalfTurn $7E); successful Steal/Mug or a
  character's Scan (ability 64) -> full (CheckCantrip). Command per unit recorded in a
  FunctionProvider.CreateActFunction prefix. Todo: failed Blitz half turn.
- [x] Steal (v0.15.0): the PR's StealExecution (decompiled) is (Lv + 50 - target Lv)/99,
  x2 with the steal relic, then GetRandWeight(MONSTER_STEAL_RATE 88/12) -> slot 1
  common / 2 rare; returns (0, slot, HitType Hit) or (0, 0, Non). GetStealStatus copies
  it into CalcResult (stealContentId 0x1C). Postfix there: BNW success (Speed*2+112)/256,
  rare slot Speed/256, empty slot fails. Monsters stealing gil: vanilla in BNW, left.
  Todo: Steal through normal evasion (CoinHelp), Mug misses when its steal fails, Blind
  on Steal/GP Rain/Jump (BlindHelp hit 50).
- [x] Dance (v0.15.0; 3.0 choreography.asm): `tools/port_dances.py` writes BNW's step table
  (CF/FE80: Shift/Repeat/Uncommon/Rare per dance) into the PR's step groups
  (ability_random_group 1-8 = on the dance's background, 37-44 = elsewhere with stumble
  ability 262 at 50): own background Repeat/Uncommon/Rare 63/31/6, elsewhere
  Shift/Uncommon/Rare 63/31/6, stumble 0 (profile dance_rules "bnw"; other mods: steps
  only). The plugin removes the Dance status (condition 51, type 619) when a Dance act
  (command 22) ends: one dance per command. Todo: BNW switches the background to the
  dance's for the rest of the battle.
- [x] Bushido (v0.15.0): the PR uses the skill's spell power as Battle Power (probe:
  Dispatch power 6 hit for ~5 at Lv 1), but BNW's power is a multiplier: BP =
  (right + left weapon + base BP x (2 if dual-wielding)) / 8 x power (GetBushPwr), then
  BNW Fight; damage hook (ability type 601) scales by BNW / vanilla. Base BP per
  character from SNES init byte 10 (bnw_data base_bp; Cyan 64). Gauntlet x1.5 with a grip
  weapon worn (2026-11-06). Todo: BNW's charge speed (the PR charges in the background).
- [x] GP Rain (GIL_THROW) Stamina*10*Level/(2*(targets+1)) and Mantra (CHAKRA)
  (current HP/64 + Level)*Stamina/4: postfix on CalcControllerProvider.GetUniqueStatus by
  ability.ProcessProg. Todo: GP Rain's cost (Stamina*10 GP), BNW's Chakra (MP
  (Stamina+Level)/2; the port turned Spiraler's slot into a fixed-HP function).
- [x] Encounter odds (data, profile game_constants): back/pincer/side 13/13/13, normal 48,
  preemptive 13 (BNW 33/33/33/156 of 255); pincer starts the party's ATB like a front
  attack (ACCUMULATION_ATB_CROSS_FIRE 50-80).
- [x] Imp (v0.16.0, `ImpRules.cs`): BNW skips vanilla's "Imp equipment" rule (c2.asm 858)
  and halves an imped attacker's damage/healing except with Item (c2.asm 4759 ImpNerf). The
  PR copies vanilla in ParameterProvider.GetArmorDefence/GetOffensivePower/
  GetWeaponOffensivePower (decompiled `tools/decomp/imp`: CheckHaveCondition(..., 601) drops
  non-Imp gear; that was the x0.63 seen earlier); while those run the Imp check answers no.
  The x0.5 is in StatusFactor (monsters too). Probe: imped attacker raw x1.0, final x0.50 for
  Attack and Fire in every direction. Todo: BNW lets Imps cast every spell (c2.asm 2245).
- [x] Blind (v0.16.0, `BlindRules.cs`): BNW (Synchysi's patch, c2.asm 1372/5384) gives a
  blinded attacker a physical hit rate of 50, can't-miss attacks included; the PR halves the
  attacker's accuracy (PhysicalExecution: hit rate = ConfirmedAccuracyRate /
  GetArmConfirmedAccuracyRate, 255 = never misses and skips Blind). In battle a blinded
  unit's accuracy now reads 100, so the game's halving gives 50. Probe (new Blind case, 8
  samples): Attack hits PE 4 -> 1, EP 8 -> 4, PP 8 -> 5 of 8; Fire unaffected. Todo: BNW
  also lets Blind affect Steal, GP Rain (CoinHelp) and Jump.
- [x] Edgar's Tools (porter `port_tools.py` + plugin, v0.16.0): the item porter only moved
  tool prices, so BNW's names ran vanilla effects (Defibrator did Debilitator's job). Now:
  tools that cast a spell (BNW Tool_Data_1/2, c2.asm 2767: Bio Blaster 125, Flash 126,
  Defibrillator 154, Mana Battery 155; profile `tool_spells`) take the ported spell's effect,
  power, element, hit and targets; others their power/hit (Chainsaw 255, Drill 200 + Sap,
  Auto Crossbow 180 / hit 100); Bio Blaster 45, Flash 60. Spell fixes found on the way:
  "!Defib" had no template (it borrowed ForceField's) -> alias to Life; "!ManaBat" borrowed
  Dischord's level halving -> an MP heal with the spell formula (function 1, values 2/3;
  profile `mp_heal_spells`), one ally. The plugin gives Drill/Chainsaw/Auto Crossbow BNW's
  Fight formula with the tool's power as BP (BattleFormulas). Already the game's: Drill and
  Chainsaw ignore Defense (IgnoreDefenseItemIds = [35, 33]), Chainsaw's 25% kill. Left:
  Auto Crossbow no-split and its Schematics upgrade, Noise Blaster's Stamina evasion. Other
  BNW spells without a template (port_report), checked 2026-11-05: Blink, !WindSlash,
  G-Force, Gi Nattak come out right (statuses/element/damage function rebuilt over their own
  slot); !Shock (SNES 130, which idmap gives the slot result 214: the Shock command runs the
  PR's ability 19 instead; 2026-11-06 the plugin's, below), Jackpot
  (slot Reflect???; harmless: the PR's 7-7-7 is its own ability 215 "Joker's Death", Death
  to all foes, and SNES slot 85 "Joker Doom" is Dispatch's slot, not a template),
  !WarpFlute (slot Sour Mouth; should warp out of battle like a Warp Stone).
- [x] Morph without a gauge (v0.16.0, `MorphRules.cs`): BNW removed the morph gauge
  (c2.asm 308, 450, 894): always available, lasts until Revert or the battle's end. The PR
  (decompiled `tools/decomp/morph`): command 8 needs MagicLearningValue > 7
  (CommandAvailableData.IsAvailable -> now true), TranceConditionFunction drains
  MagicLearningValue x 0.5/1 by time unless isPermanent (now set in Start: gauge 100, no
  drain). Probe: the hook fires when Morph is applied; Morph damage factors unchanged.
- [x] Setzer's Slot (v0.17.0, `tools/port_slots.py` + `SlotRules.cs`, config [Battle] SlotRules).
  The PR (decompiled `tools/decomp/slot`): Serial.FF6.Management.SlotInfomationData holds the three
  reels as List<SlotDesignPattern> (Diamond 0, Chocobo 1, BAR 2, Airship 3, Bahamut 4, Seven 5; the
  SNES C2/A800 reels in the same order), per-symbol 1-in-N rolls for reels 2/3
  (IsHit{Second,Third}DesignLottery: reel 2 Seven 8, Bahamut 64, BAR/Airship 256; reel 3 Seven 32)
  and GetHitAbility: 3 of a kind -> {Diamond 213, Chocobo 211, BAR 214 (random esper, group 9),
  Airship 212, Bahamut 112, Seven 216 "Joker's Death" on the foes}, 7-7-Bar -> 215 (Joker's
  Death on the party), else 210. BattleSlotReelController.StopReel(isHit, design, isEyeCatching):
  a passed roll slides the reel up to 5 symbols onto the match; a failed one slips reel 3 one
  past it. BNW 3.0 (checked in the ROM, synchysi slots.asm): reels without Bahamut, symbols in
  threes; no rigging (C1/806D, C1/80A6 branch over it); 7-7-Bar can't come up (C2/B4AF); 7-7-7
  casts Jackpot (spell 151, heal power 10 on the party, lifts Death). Now: the porter makes 216
  Jackpot (functions lift Death + revive to max HP/8 as Life does + Cure's heal, power 10, the
  party, Cure 3's animation, its own name key: 215 and 216 shared MSG_MAGIC_NAME_144) and 215 a copy
  of the losing spin (Go Fish); the plugin refills the reel lists with BNW's (bnw_data.json
  "slot", read from the ROM; only if the game's lists are vanilla's) before the slot UI is built,
  and StopReel runs with isHit false / isEyeCatching true (no slide, no slip). Headless check
  (`probe_headless.ps1 -Mods Magicite -Until "\[slot\] reels"`): reels BNW, 7-7-7 -> 216
  (function group 2068, power 10, party), 7-7-Bar and losses -> Go Fish, rigging off. Not
  seen in a real battle yet. The SNES Jackpot slot itself (PR 152, a Lore) still carries the
  Reflect??? effect; nothing casts it. Bar-Bar-Bar (port_slots.bar_espers): the PR's 214 rolls
  ability_random_group 9 (25 espers at 4, Odin/Raiden out, Bahamut via its own copy 1532); BNW
  rolls any of its 27 (C2/37DC "LDA #$1B; JSR random; CLC; ADC #$36", read from the ROM). Group 9
  is now BNW's 27 (weights 19 x 4 + 8 x 3 = 100), Bahamut still via 1532, which takes BNW's
  ported Bahamut (power 150). Headless: "Bar-Bar-Bar espers 27 (total weight 100)".
- [x] Runic and Bushido weapons (v0.17.0, `tools/port_flags.py` + `CommandWeapons.cs`). BNW (readme): Runic absorbs every
  black/white/grey spell and no enemy skill; it needs a weapon that allows it ("Swordless Runic").
  The PR (decompiled `tools/decomp/runic`): MagicSealSwordConditionFunction.ForciblyTarget draws an
  act to the Runic user when its ability has **content flag 3**, which matches the SNES "allow
  Runic" bit (spell byte 3 bit 3) on 247 of vanilla's 249 spells (the PR leaves X-Zone and Imp
  out). port_flags now carries that bit like the ignore-defense one (profile.runic_flags "mod":
  BNW's 54: every black/white/grey spell + Leo's Shock; set Quake, Storm, X-Zone, Meteor, Merton,
  Imp; cleared 15 enemy skills such as Atomic Ray, Giga Volt, Blight, Exploder). The weapons:
  Serial.FF6.Management.BlockMagicSwordData.ValidWeaponContentId (code, exactly the SNES weapons
  with byte 19 bit 7: 51 incl. spears, katanas, Magus Rod); the plugin refills it with BNW's 23
  (daggers, swords, Atma Weapon; bnw_data.json "runic_weapons") when it holds vanilla's (config
  [Battle] ItemEffects). Bushido is the same: Serial.FF6.Management.KillingSwordData
  .ValidWeaponContentId (command 12) = exactly the SNES weapons with byte 19 bit 1 (28); BNW's 14
  (decompiled `tools/decomp/bushido`; bnw_data.json "command_weapons"). Headless: "[weapons] runic
  BNW's 23 (was vanilla's 51); bushido BNW's 14 (was vanilla's 28)". Not checked:
  the MP gained (BNW keeps the SNES spell MP cost / number of Runic users; the PR sets it in
  MagicSealSwordConditionFunction.SetRecoveryValue, caller not found), BNW's elementless and
  Clear-keeping absorb (readme "Non-Elemental Runic", Vanish fixes), the ready stance.
- [x] Spell "can't miss" and "no split damage" (`tools/port_flags.py`, 2026-11-05): the PR's content
  flag 1 = can't miss matches SNES spell byte 4 bit 5 on 243 of vanilla's 249 spells, flag 2 = no
  multi-target split matches byte 2 bit 6 on 246. Neither was ported: BNW changed 87 / 52 of
  them (Blitzes and Bushido can miss; Quake, Ultima, Merton, Blizzard, Mantra don't split; some
  summons do), and spells rebuilt on a borrowed template had the template's (Umaro's Blizzard
  lost "can't miss"). Now carried like ignore-defense (profile hit_flags / split_flags "mod" for
  BNW: 35 set and 36 cleared / 40 set and 6 cleared; "changed" for the other mods: no change).
- [~] Umaro (v0.17.0, `UmaroRules.cs` + profile ability_overrides). Headless startup check:
  "BNW odds set 0: 1=70% 201=30%; set 1: 1=41% 200=29% 201=30%; set 2: 1=41% 199=29% 201=29%;
  set 3: 1=10% 199=30% 200=30% 201=29% | GetRand hook ok", every pick's roll lands on it in the
  PR's table; not yet seen in a real Umaro battle. BNW (C2/5269, readme "Umaro Hit Hard"): per relic set (none /
  Rage Ring / Blizzard Orb / both) weights of 255 for Fight, Tackle, Snowstorm, ally toss:
  178/75/-/-, 102/75/-/75, 102/75/75/-, 26/75/75/75 (vanilla SNES 158/95, 94/63/95 ..., the PR
  50/50, 25/37.5/37.5, 13/29/29/29). The PR (decompiled `tools/decomp/umaro`,
  ActSelectUmalo.SetActDataUmalo) builds that table per umaloType inside the method and rolls
  CommonUtility.GetRand(100); the plugin picks with BNW's weights and answers that one GetRand(100)
  with a roll that lands on the pick (GetRand(int) is 12 bytes, a jump to Random.Range; the
  startup check logs whether the hook answers). Magitek armor keeps the PR's. Data: Tackle (PR
  201 "Slam", already ignore-defense) power 190 -> 255, ally toss (200) 250 -> 382 (255 + Gauntlet).
  Blizzard (199): power 90, no split, Spirit-based, Blind (spell data). Checked 2026-11-07:
  Tackle already respects row in the PR (CalcControllerProvider.GetFightStatus ignores row only
  for ability type 602, Blitzes, and machine items) and Blizzard's Blind gets no Stamina check
  (AddConditionFunctionSerial.CheckForceMissCondition has none; the plugin's Spirit evasion
  leaves Blizzard out). Left: the toss of Mog as a sure critical (C2/16D6; the damage step
  doesn't know who was thrown).
- [x] Sketch needs a brush (v0.17.0, `CommandWeapons.cs`, config [Battle] ItemEffects). BNW "Brushless
  Sketch" (c2.asm SketchBrshChk/BrushHand: an item whose name starts with the brush icon $E2 in a
  hand; BNW's 4 brushes, content 155-158, bnw_data.json "sketch"). Postfix on
  CommandAvailableData.IsAvailable (command 18) turns Sketch off without one. Headless (latest
  save): "Sketch off for ...: no brush". The PR's Sketch (decompiled tools/decomp/commands,
  ActSelectSketch/SketchConditionFunction) makes Relm the attacker of the sketched monster's
  ability, so her level, Magic and Battle Power are already used (BNW's "Sketch uses Relm's
  stats"). Magic for Vigor (v0.20.0, BattleFormulas): a sketched physical attack (calc under the
  Sketch command) runs BNW's formula with her Magic doubled in 8 bits as Vigor (c2.asm
  SketchMag, BattleMath.SketchVigor: Magic 128+ wraps, as in BNW).
- [x] True Knight cover (v0.20.0, `CoverRules.cs`, 2026-11-07; see the progress row: a postfix on
  TargetFunction.ProtectExtractTarget, BattleMath.CoverGuard, the probe battle's self-test; the
  research below). BNW (c2.asm 968) covers a Near
  Fatal ally always and a back-row ally with Stamina/192 (better when Defending), only from the
  front row, not when the guard is Near Fatal, with a longer status blacklist, single-target
  attacks only, the guard with the most HP, no monster guards. The PR decides in
  Last.Battle.Function.TargetFunction.ProtectTarget(act, otherSameParty, target, protected,
  protectUnit) (decompiled `tools/decomp/cover`) through three lambdas (<>c.b__15_0..2), then
  SetProtectData (since 2026-11-07 the Ghidra script writes <>c classes too: "()c" in the file name). A
  replacement needs the row and status checks (done) and a played fight with a True Knight (the
  back-row Stamina cover is still unseen in one).
- [x] Steal through evasion (v0.16.0, CommandRules): BNW's CoinHelp (c2.asm 2773) sends
  Steal (and GP Rain) through hit determination with hit rate 255, 50 when blinded: before
  BNW's success roll, Steal now misses unless 255 (or 50) * (256 - 2*Evade) / 256 >
  random(0..99), Evade = the monster's evasion. Not probe-measurable (logged once).
- [x] nATB is native to the PR (decompiled BattleProgressATB.Update, `tools/decomp/natb`): the
  whole ATB update (status timers via BattleConditionController.Update, gauges, charging)
  returns while BattleActExection.GetStatingAct() or !BattleUtility.IsStagingEnd(). So BNW's
  shorter timers apply as they are: Sleep/Stop 30 -> 15 (BNW 9 of 18), Freeze 60 -> 18 (BNW
  10 of 34, profile condition_tuning). NatbRules stays as an option, off by default.
  **Doubtful (found 2026-11-12 in the WC work, not re-checked for BNW):** FF6 runs its own
  BattleProgressATBFF6.Update, which doesn't make that check: it stops only for
  BattleUIManager.IsWaiting / IsForceWaiting. Measured headless in a WC battle (WcTest turn_cycle,
  Active mode): no stopped time across whole turns, the attacks included (gauges, charging and status
  timers ran on). So BNW's nATB is probably not native after all, and NatbRules (off) may be needed.
- [-] (superseded) nATB hook (v0.16.0, `NatbRules.cs`, config [Battle] nATB): time stops while an action plays
  (c2.asm 873, c3.asm 2542): BattleProgressATB.UpdateGaugeIncrease (ATB fill) and
  UpdateChantTime (spell charging) are skipped while BattleActExection.nowState is
  InPrepare/InAction/InAfterAction/CounterAct/CounterAI; never more than 15 s in a row
  (safety). Unverified: the probe battle never refills ATB (a command menu stays open), so
  the hook never ran there; the first real battle logs "[natb] time stopped ... s".
  Todo: BNW's shorter Sleep/Stop/Freeze timers (c2.asm 3608-3628) and status timers
  pausing too.
- GP Rain (investigated 2026-11-05, `tools/decomp/gilthrow`): the PR's cost is Level x 30
  (capped at the gil owned), BNW's Stamina x 10, both inside
  UniqueUpdateParameterProvider.HitCalcGilThrow (by-ref dictionary parameter: not
  patchable); the spent gil goes through BattleAddExpPlug.AddPlayerSideLostGil(int), which
  monsters stealing gil also use. HitCalcGilThrow also computes the damage itself
  (thrown x 2 / targets): whether our GetUniqueStatus formula reaches real battles needs a
  played GP Rain ("[commands]" log).
- [~] Dual wield and Offering (v0.16.0, RelicRules): BNW puts both on weapons (abilities
  252/253); the PR keys its Genji Glove / Offering corrections on accessories BNW turned
  into consumables (decompiled `tools/decomp/relics`: IsDisorderlyShootAttack = accessory
  armor 114; GetAccessoryPhysicalCorrectionValue 0 -> 0.5, 1 -> 0.75 if IsSecondlyAttack,
  2 -> 1.25, applied to Fight). Now: type 1 returns 0.75 when holding two weapons (BNW's
  Genji effect is on only for weapon/weapon, c2.asm 799-844), type 0 returns 0.75 with an
  Offering weapon (c2.asm 2104-2125: each -25%, multiplied), IsDisorderlyShootAttack is true
  for Offering weapons and CreateContinuousActData's strike count 4 -> 2 (c2.asm 1099).
  The probe party had no such weapons: the paths run (logged per character at probe start)
  but the values are unseen in a real fight.
- [x] The two-handed grip (v0.18.0, RelicRules; 2026-11-06). The PR: PlayerCharacterParameter
  .ConfirmedAttack and .GetArmConfirmedAttack (natural + added + weapon attack) multiply by
  EquipmentAbilityData.TwoHandedMagnification (2) when OwnedCharacterData.HasTwohand: the
  character has ability 251 (EquipmentAbilityData.BothHandsAbilityId) and one hand holds a
  weapon off the InvaliteTwoHandeds list (23 content ids, vanilla's non-grip weapons: daggers,
  rods, brushes...) with the other hand empty (decompiled tools/decomp/twohand: the .ctor fills
  the list, 2 and 251). BNW (c2.asm 799-850 WeapChk, 4914 PlayerPhys): Battle Power x1.5 for a
  weapon with the grip bit (byte 19 bit 6) and the two-hand effect (relic byte 12 bit 3), no
  shield or second weapon; only its 15 grip weapons have both (Mythril Pike, Trident, Stout
  Spear, Partisan, Longinus, Fire Lance, Gungnir, Hanzo, Kotetsu, Ichimonji, Kazekiri,
  Murasame, Masamune, Mutsunokami, Excalibur), and the port gives 251 to exactly these (the
  PR's Gauntlet, armor 111, keeps it, but BNW has no Gauntlet: its slot 208 is a blank item;
  test_mod checks all of it). So the game's check is BNW's and only the factor changes: while
  the game computes the attack, HasTwohand answers no, and the attack gets x1.5. Bushido: x1.5
  with a grip weapon worn, shield or not (C2/185B BushidoCommand checks only the relic bit).
  Windslash / Aero (byte 7 bit 3 "Respect Weapon Flags", c2.asm Wpn_Chk): x1.5 gripped, else
  x0.75. Startup line "[grip] check" (each grip-weapon holder's attack, BNW's and the game's).
  Callers found with pr/tools/ghidra/callers.py (direct calls to a method's RVA: the managed
  code is GameAssembly.dll's "il2cpp" section, not .text).
- [x] Shock (v0.18.0, BattleFormulas; 2026-11-06). The PR's Shock command (28) runs ability 19
  (type 618, power 128, a magic hit). BNW (c2.asm C23EDD): 3/4 (Level x Stamina + current HP)
  in 16 bits (sums past 65535 wrap, as in BNW), halved from the back row, ignoring defense
  (port: content flag group 11 = flags 1 + 5), the user taking max HP / 8 (BNW shows it as a
  number; here the HP drops a frame after the hits, KO through the condition controller).
  Used by Leo and by Celes with Leo's Crest (Runic -> Shock, relic bit 10.4). Formula probe:
  79 of 79 cases on BNW's numbers; the cost needs a real Shock.
- [ ] Relic damage corrections, rest (decompiled CalcExecuteCorrection
  .GetAccessoryPhysicalCorrectionValue(type): 0 Offering x0.5 on Fight, 1 Genji x0.75,
  2 Atlas x1.25, 3 Gauntlet; 0 = not worn). The game counts only **accessories** from its
  PhysicalCorrectionAccessoryIds list, but BNW put these bits on weapons: two-hand (byte 12
  bit 3) on spears/katanas, dual wield (12.4) on daggers/swords, Offering (12.0) on
  Kagenui/Daryl's Soul. Done since: Offering and dual wield (types 0/1, v0.16.0) and the
  two-handed grip, which the PR applies to the attack itself, not here (type 3 stays the
  vanilla Gauntlet's, which BNW doesn't have). HardItems' dict rule (one Atlas bit for all
  4 keys) is wrong for keys 0/1/3.
- [ ] From the BNW command research (2026-11-05, see the agent notes below): failed Blitz
  half turn (no failure path found in the PR), Mug misses when its steal fails, Steal
  through evasion, Blind hit 50, Tools (Chainsaw 25% kill/ignore Def, Drill, Autocrossbow
  no split...), Sketch with Relm's Magic for Vigor on physical sketches, Leap rules (done
  2026-11-07), X-Magic one counter (done), Mimic memory (done), Umaro table, Rage clearing innate statuses, Shock
  3/4(Lv*Stamina+HP) and self-damage, Morph (no timer; Terra Morphed after Phunbaba;
  attacker +50%, target x2(255-min(Sta,96))/256+1), Imp attacker x0.5, character on
  character x0.25, Offering 2 hits -25%, Gauntlet x1.5, back-attack bonus removed, cover
  halves Evade, nATB, D7 elemental averaging. The probe now measures Imp/Morph/Defend/
  Safe/Shell and character->character hits (`probe_report.md` "Measured modifiers").

## Formations, shops, chests

2026-11-06 (BNW re-port): chests 159 PR chests in 93 map files, 5 of the PR's scripted chests
filled (the game's own Tag / SetGilTag + "Obtained" line + GetItem, as vanilla's pick-ups),
6 boxes with BNW's monsters on their box's background. Shops: 82 matched + 10 PR copies;
SNES shop 86 is new in BNW (vanilla's is unused): a merchant NPC BNW added in Doma Castle (SNES
map 126, NPC 16, WoR). The PR opens each shop with its own SysCall ("ショップを開く:<town>...",
one game method per shop with its group id), so a new shop needs a new NPC and a plugin call.

2026-11-07: the rest. The merchant (tools/port_merchants.py, profile added_merchants; BnwPlugin
ShopCalls.cs): an NPC copied from South Figaro WoR's shopkeeper, placed by the profile (BNW's
spot is behind a wall in the PR room), script 9901 = the shopkeeper's script with SysCall
"BNW:shop <group>", which the plugin answers with the game's Current.OpenShopCommon(group,
isFirstClock). Every new script must be in its map's entry of the map group's package.json
(pr_script.list_in_map): a map loads only the scripts listed there (MapModel.GetScript), and
the field looks scripts up there by name. The six chest changes left were four SNES places with
no vanilla chest and two item chests made boxes. By event bit: map 44's Moogle Charm moved two
tiles up (inside the PR room's wall; the PR's chest keeps its spot, same contents); map 72's
1000 gil is the third SNES copy of a chest already ported (bit 97: maps 68/69/72); map 414's is a
copy of map 296's (bit 235; no exit or event leads to map 414, and its copy holds BNW's blank
item 208); map 408's box moved from its twin map 406 (bit 205). port_chests.moved_chests: a
mod's chest at a new spot whose bit is exactly one vanilla chest's that the mod dropped is that
chest. The two boxes (South Figaro's Earring in both PR copies, Mt. Zozo's Red Cap) get box
scripts of their own (port_chests.new_box: the PR's box script sc_e_0641 opening the chest's
entity, its after-battle script with only the fade-in; script ids 9801-9803; listed in their
maps' package.json), fighting on the map's random-encounter background (map.csv
monster_set_id's first party). Box battles: a box took idmap.party(formation), vanilla's party
for that formation number, but BNW renumbers and edits formations (its 351 is vanilla's 465,
Katanasoul; its 256 is a Dinosaur where vanilla's is four other monsters), so
port_chests.seed_party now uses a vanilla party with BNW's line-up, else a new one from the
encounter porter (party_for). WC seeds don't edit formations: unchanged. Headless (FormulaProbe
"shopcheck=<map>:<point>:<script>" and "boxcheck=..."): the shop opens, both box kinds start
their battle with BNW's monsters.

The Veldt (tools/port_veldt.py): the PR's table is the misc asset's beast_field_encount_table,
group N = SNES formation block N - 1, one entry per formation (flag_index, monster_party_id,
encount_target_party_id + encount_target_other_ids: the parties whose battle opens it). The SNES
offers any met formation its flags allow ($F5900 + 4f, byte 3 bit 1: not on the Veldt); BNW
allows 74. The mod ships its copy with a keys file (misc/keys/mod_misc.json), as battleextradata.

Chests, 2026-11-05 (later): matching rewritten (`port_chests.match_flags`). The old content-only
match let one place's chests take another's when they share common items (SNES map 226's
Running Shoes landed on map 378's chest in PR map 20241, so BNW's item for one place went into
another's chest) and filled only one copy of places the PR keeps twice (Figaro Castle
20051/20371, South Figaro 20071/20361...; the copies share the chest's flag_id). Now: per SNES
map and PR map file, the offset under which the most of its chests sit on same-contents PR
chests; every chest of the map (empty and monster-in-a-box too) takes the flag at its spot
(every matched content chest lands on vanilla-equal PR contents); the content match stays as a
fallback inside groups the layout tied to that map; where the PR merged a place's two SNES maps
into one set of chests, the chest the mod changed wins. Writes go to every entity of the flag;
a vanilla monster-in-a-box that BNW made a plain chest loses its battle script (Zephyr Cape,
Umaro's Bone Club, a Dried Meat). BNW: 121 of its 129 item/gil chest changes placed (was 118,
some at the wrong spot), 135 PR chests written (was 118); left: 5 with no PR chest near
(Psycho Belt, Phoenix Down, Green Beret, 1000 gil, Wing Edge) and 3 on chests the PR shares
with another changed chest.

- AP: SNES magic-point table 0x1FB400 (512) → `monster_party.get_value` (PR id = formation + 1).
- Formations: SNES 0x0F6200 × 15 bytes (mold, slot mask, ids, positions, id msb);
  455/576 match PR party f+1 by enemy set. BNW reshuffled 559 of 576 formations.
- Shops: SNES 0x047AC0 × 9; PR `product` rows grouped by `group_id`; matched by
  stock overlap (82 exact). Extra rows are parked in group 9999.
- Chests: SNES ptrs 0x2D82F4 → data 0x2D8634 (5 bytes: x, y, bit, type|bit9,
  contents; type by priority item 0x40 > gold 0x80 > monster 0x20 > empty 0x08).
  PR chests are `entity_default.json` objects (action_id 6, content_id = content.csv
  id, 1 = Gil with content_num). PR flag_ids are renumbered, so chests are matched
  per map by contents (`tools/port_chests.py`, 222/233), then by position (16 px tiles,
  per-map offset from the paired chests). The 13 BNW chests left have no PR entity of their
  own: two SNES chests share one spot where the PR has one entity (e.g. bits 116/257 on map
  378, World of Balance/Ruin versions), or vanilla's chest was empty/monster and the PR has
  none there. Adding chests needs new entities and save flags: not done (known limit). Modified map files are
  written whole, keyed from `vanilla/assetspath/.../AssetsPath.json`
  (map keys look like `Map_20011_1/entity_default`).
- Enemy slots: BNW renamed/re-statted 288 enemy slots mostly in place (same sprite).

## Enemy AI (tools/snes_ai.py, ai_compile.py, port_ai.py)

BNW restructured 377/384 SNES AI scripts, so they're compiled, not patched.
PR AI JSON (`vanilla/monster_ai/.../MonsterAI/*.json`) is run by
`Last.Interpreter.BattleAICore`; each mnemonic is a static handler on
`Last.Interpreter.Instructions.BattleAI`. Model: `Main` → Is* checks jump to
labels (operand = mnemonic index) → `StartTable`…`EndTable` with one `Act` row
per turn (cycled), `CounterAct*`/`FinalAttackAct` rows active for that table →
`JumpBattleAI`. Conventions: SNES "Battle" → monster's own type-4 attack ability
(from its vanilla script, default 1); "Special" → the monster's own special
(port_specials.special_of: the type-3 ability its PR AI / Control group uses, or a copy
from 7700 where BNW split a shared one; it was 377 + SNES enemy id until 2026-11-07, right
for 25 monsters only);
"Nothing" → 696; HP ≤ n×128 → `IsCheckHp [n*128]`; timers in SNES units; SNES
cmd Fight→4, Magic→2, Bushido→601, Blitz→602, Steal→607.
v1: regular enemies compiled to `sc_ai_bnw_NNN` (monster.script_id = 3000 +
SNES id). Approximations: several random actions in one SNES turn become
consecutive turns; targeting and battle messages are dropped; unsupported
conditions skip their branch. PR dropped SNES slots 164/170; BNW's Bio Blast/Boil
get new ability + content rows there.

v2 (boss work, 2026-11-04): 320 compiled (271 regular + 49 bosses, 24 of them hybrids).
- Variables: PR AI variables are 0..14. SNES vars used whole keep their index;
  each tested/set bit (`FC 14/15 VV BB`, `F9`: BB is a bit NUMBER) and whole vars
  above 14 get a free PR index from the top. Setting a whole var also sets its bit
  vars (v1 kept them apart, so "first turn only" flags never cleared).
- Timers: `FB 00`/`FB 04` (reset monster/battle timer) after a timer check -> the
  branch's first action is `TimerAttackAct [0, ids] [0, rates]` (acts + resets),
  the PR's own idiom (Dadaluma, Inferno).
- Death: "monsters die like a boss" (`F5 xx 01/05 FF`) on death -> `FinalAttackAct
  [735]` (SM_ERASER: everyone vanishes, battle ends), as the PR's boss scripts do.
- Counters that change variables -> Main-section routes, like the PR's scripts:
  `IsCheckReceiveCommand [cmd, L]` / `IsRecevieAbility [ability, L]` /
  `DamagedJump [L]` -> table with the counter attack + the variable updates.
- `FD` (wait) inside a condition block continues the block next turn (until its
  `FE`): a branch can be several turns (v1 ended the block, leaking the rest into
  the default rotation). `F9 00` bit toggle = compare-and-jump (the PR has no XOR).
- `F5` modes: 00 bring in (HP restored), 01 kill, 02 bring in (HP as is), 03 hide
  (HP restored), 04 hide, 05 die like a boss; mask = formation slots, 0 = self.
  "Bring in" is resolved against every BNW formation the monster appears in
  (`port_ai.formation_slots`): slots that don't exist are no-ops (Heartfire); a
  monster bringing itself back on death right before "VAR >= n: die like a boss"
  with n reached in that same pass is FF6's trick for the boss death animation ->
  FinalAttackAct 735 (all eight legendary dragons). Real extra lives (Isis, Myria:
  n = 2) and real summons stay unsupported. The PR summons with special abilities
  made of battle function 20 = (add monster id, grid cell) entries (up to 3 per
  ability, e.g. 704, 708), guarded by alive checks/variables in its scripts; every
  boss that genuinely summons in BNW is also tied to a PR battle-event routine.
- `FA` animations are cosmetic: ignored. In a death block, `FB 0B 07` (give
  myself Death) is dropped, and `FB 02` (end battle) = erase all if death is the
  only condition, else dropped (the fight ends once everyone is dead: Crane).
- Still unsupported (script keeps the PR's): real summons, hiding/killing specific
  monsters (`F5` 01/03/04 with a mask) outside hybrids, extra lives, events
  (`F7`), command changes (`F4`), formation change (`F2`), end battle / self-Death
  outside death blocks.

Bosses (`port_ai.BossPolicy`): 49 of 88 changed bosses run BNW's AI. The PR
moves many boss mechanics into hard-coded routines called from the fights' battle
event scripts (`Last.Interpreter.Instructions.SystemCall.Current`: IfritMoveCheck,
HiddenEnemyCheck, ChadarnookSwich, No128RebornCheck, AirForceDeadCheck,
TentacleBattle, CurlaxsBattle, InfernoEnemyCheck, OrthrusMove*, CheckSoulSaver,
AnglerWhelk*, Deathgaze HP...). Their code isn't readable here, so the event
scripts were read by hand and sorted:
- cosmetic/flow only, or checked to ignore the boss's AI (`GENERIC_SYSCALLS`,
  `SAFE_SCRIPTS`: Veldt/Gau, Cultists' Tower statuses) -> normal compile;
- the routine owns who is on screen (`ENTRANCE_SCRIPTS`: Chadarnook, Hidon, Inferno,
  Air Force, No.128, tentacles, Dream Stooges, Ultros at the Esper Cave, Soul Saver)
  -> hybrid: BNW's AI with its own F5 bring-in/hide/kill left out
  (`Context.pr_entrances`), the PR routine handling entrances (24 bosses; F9 log
  marks them). UNTESTED: if a routine also reads the PR AI's variables, its
  mechanic (e.g. ghosts appearing) may not trigger; the fight should still end.
- everything else keeps the PR's script: the PR script sets modes (Vargas, Hell
  Angel, Doom Gaze, Kefka 298, Ultros 300/302, event 371) or uses abilities that
  write battle variables a routine reads (`COUPLING_PROGS`: Ifrit/Shiva, Asura,
  Isis), story event scripts (Vargas/Bear, Phunbaba #4, Kefka 372, Nerapa timer,
  Intangir 281, Soul Train), the final battle's 9 tiers, monsters only brought in
  by another fight (Chupon, Rizopas...), Dadaluma and Guardian
  274 (`F4` command), Myria (extra life), and 3 whose PR script isn't extracted.
  Rules applied to every boss: it has a formation of its own, and every other
  changed boss in its fights compiles too (all-or-nothing).
  Decompiled (2026-11-05, `pr/tools/ghidra/boss_routines.txt` -> tools/decomp/bosses): 17
  routines (IfritMoveCheck, ChadarnookSwich, No128RebornCheck, TentacleBattle,
  CurlaxsBattle, HiddenEnemyCheck, InfernoEnemyCheck, AirForceDeadCheck, CheckSoulSaver,
  NerapaDoomAttack, Orthrus*, AnglerWhelk*). They talk to the bosses through the AI
  variables (`SetEnemyCurrentVariableArray`) and battle-script flags (`DataStorage`), and
  some force acts (`InstanseActAbility`), so the remaining bosses need, per boss, the
  variables its routine writes kept out of (or read by) the compiled BNW AI.

Testing: plugin F9 (with [Testing] TestKit) starts the next BNW-AI boss fight from
the field via `FieldMap.fieldController.ExcuteEncount(party, bg)`; F10 in battle
defeats every enemy left (`BattlePlugManager.Instance().GetEnemyUnits()`: HP 0 +
`BattleConditionController.InterruptAddCondition(unit, UnableFight)`), so the fight
ends as a normal win whatever the party's level. Tried: the win screen shows; EXP and
gil are paid only for monsters on `BattleAddExpPlug.SetExpditionMonsters`, which
normal kills join, so v0.8.3 adds each F10 kill there too (v0.8.2 paid 0 EXP).
Still 0 in v0.8.5 against Holy Dragon (monster 37: 0 EXP in vanilla, 5000 in BNW):
vanilla bosses give no EXP (111 of 113 have 0) and the result came out 0 with the
dragon on the list, so the PR seems to leave bosses out of its total. v0.8.6
(`BattleRewards.cs`, config [Battle] BossExp) prefixes `BattleResultProvider.Genelate`:
if the EXP of the monsters on `expeditionMonsters` beats `GetExp(partyId)`, it adds
the difference (through `CheatSettingsClient.GetResultExp`, the Boost setting) to
`expValue`, so levels, EP and EL follow. The 5 formations with capped called-monster
EXP (`monster_party_max_exp`) keep the game's figure. F10 also records its kills'
EXP as a fallback. Logs "[rewards] formation N: game EXP ..." for the first 3
battles and every corrected one (to confirm the boss theory).
v0.8.6 log: list [37, 37] (the game adds a dead monster itself, so F10's own
SetExpditionMonsters doubled it: removed in v0.8.7), GetExp 10000, yet expValue 0,
and `BattlePlugManager.MonsterParty` already null when the result is built. So the
zeroing is in the caller (`SystemCall.Current.GetBattleResult`), not GetExp; either
bosses or F9-started fights (no MonsterParty?) get 0. v0.8.7 raises expValue to the
game's own `GetExp(party)` total, with the formation tracked during the battle
(`BattleRewards.Track`, every 10 frames) so the 5 capped formations keep their caps.
Tried v0.8.7 on the Deck: "game passed EXP 0, its total 5000 -> paying 5000 EXP";
4 in the party, so 1250 XP each, (1250/8)*10 = 1560 EP; from EP 5887 (test kit) to
7447: EL 11 at 5888 and EL 12 at 6912, so EL 10 -> 12 is right. MonsterParty was -1
for the whole F9 fight, so F9-started fights lack it; whether a boss met normally
also gets 0 is still open (the first 3 "[rewards]" lines of a session will say).
Formation reward columns: `get_value` = Magic AP (matches SNES AP for all 463
formations); EXP/gil come from the monsters. Battle result hook confirmed in game:
SP for EP = `BattleResultData.GetMp` (the "Magic AP" total).

### More conditions (2026-11-05): skipped branches 189 -> 86

The compiler dropped a branch whenever it couldn't translate an SNES condition. Matched
against the game's own scripts (vanilla monsters that use both forms), it now emits:
- FC 08/09 44 st (a random party member has / lacks a status) -> `IsCheckUnitStatus [1,
  condition type, 1/0, J]` (Earth Dragon: Float -> [1, 409, 1, J]; the type comes from
  condition.csv for the status's condition id);
- FC 0F 44 n (a random party member's level >= n) -> `IsLevelCheck [1, n, J]` (Silver Lobo);
- FC 1A 36 elements (I'm weak to one of them) -> `IsWeakCheck [PR element, J]` per element
  (Number 024 checks 1-8 = PR attribute ids: ELEM_ATTR);
- counters "hit by element" (FC 04) that change variables -> `IsReverseAttribute [PR
  element, J]` (Number 024's "炎属性の攻撃を受ける" branches);
- counter blocks that start with a plain condition ("if VAR001 >= 3") run on every hit, as
  in the SNES: their own trigger first if they have one, else "any hit" (DamagedJump).
70 compiled scripts use the new checks (36 status, 28 element hits, 10 level, 2 weakness).
Still skipped (68 in the compiler, 86 in the port): "monster in slot" (FC 19) and "these
monsters alive/dead" (FC 11/12): the PR's IsEnemyUnitIndex / IsUnitAlive number units per
formation, not by SNES slot mask.
2026-11-05 (later): FC 1B "this battle is formation a + 256 b" -> IsCheckMonsterPTId [PR party,
J] (vanilla Chaser: FC 1B 7B 00 -> 123 and the PR copy 3088) for party f + 1 (the PR numbers
parties from 1: 255 vanilla formations match), the party port_encounters made for a BNW
formation (2000 + f) and the PR's own copies of party f + 1 (ids past 576 with the same
monsters, at most 6 checks): skipped 86 -> 81 (WC 65). FC 19 can't follow: IsEnemyUnitIndex is
the PR party's monster column (Opera Ultros: SNES slots 1/2/4/8 -> columns 5/4/9/3), and the
PR hand-placed its formations (nearest-cell placement predicts the column for only 605 of
1264 vanilla monsters). tests/test_mod.py checks mnemonics, operand shapes and
jump targets against the game's own scripts (passes). Not yet seen in a battle: the probe
run on formation 4 (two monsters with the new checks) was interrupted.

### Battle lines (2026-11-05, `tools/battle_text.py`, profile battle_messages)

The SNES AI's "message xx" (F3 xx yy) shows monster speech from one table: C1/9846 `LDA
$CFDFE0,X` (pointer read from that instruction), menu encoding, $01 new line, $05 pause, $02 nn
= character nn's name, BNW's $D3 opening quote and icons ($D0/$D1 arrows, $D6 the confirm
button, $D5 a music note). BNW renumbered the table (its #0 is vanilla's #12, Ultros). The PR
shows battle lines from story_mes_en keys given in an act's sValues (Cactuar: Act [696]
s[M109_01]; Ultros: FirstAttackAct with three keys in turn), speaker in story_cha_en (empty for
monsters), names as codename tags "(MASH)". Now each message line becomes BNW_BTL_<msg>_<line>
in story_mes_en/story_cha_en (port.write_messages writes extra text files; Magicite merges
them: headless, the game has both BNW_BTL_018_01 "BARF!" and its own E0055_02_343_b_13), and
the compiler puts a turn's/counter's/death's lines on its first act (a block that only talks
gets a do-nothing act, 696). 45 lines in the compiled scripts (dragons, Ultros, the Stooges,
Atma, Crane, Guardian's song...); 41 on Act/FinalAttackAct/TimerAttackAct (types the game uses
with lines), 6 on CounterAct/CounterActReceiveCommand (the game has no example: may stay
silent). BNW only (WC's live seeds apply system_en only). Not seen in a battle yet.

### Bosses still on the PR's AI (39, analysed 2026-11-05)

From port_report: 9 final-battle tiers (policy), 7 whose PR script sets/counts modes for
their battle event (Vargas 259, Tunnel Armor 260, Deathgaze 285, Kefka at Narshe 298, Ultros
300/302, an event monster 371), 6 with special battle-event routines (Phantom Train 262,
timers 280, King Behemoth 281, 333, Phunbaba 364, 372), 4 Ifrit/Shiva/Vargas variable
couplings (264, 265, 295, 296), 4 without a formation of their own, 3 PR scripts not found,
5 using commands/events/enemies/self-revival, 1 sharing a fight. The mode group looks
closest: `ModeCount` is a per-turn counter the battle event reads, but it sits only in some
phases (Tunnel Armor and Narshe Kefka count only while above an HP threshold; Deathgaze every
turn; Ultros 300 on its final attack). A hybrid (BNW's compiled AI + the PR's ModeCount at the
same phase) needs the battle event read first (what the count triggers) and a played fight to
check; not done unattended.

## Testing (pr/tests/test_mod.py)

No playtesting by driving the user's screen (they asked not to). Offline suite:
`pr/.venv/Scripts/python -m unittest discover -s pr/tests -v` (21 tests): CSV
headers/rows, keys files, text lines, every id the mod writes resolves after
merging over vanilla, AI scripts vs the operand shapes/jump rules of the game's
own 430 scripts, chest map files differ from vanilla only in chest contents,
and BNW spot checks (Terra, growth, Ramuh, Sap, esper rules, stat caps).

### Decompiled game code (`pr/tools/ghidra/`, user OK'd the downloads 2026-11-05)

Il2CppDumper v6.7.46 (`tools/dl_il2cppdumper`) on GameAssembly.dll + global-metadata.dat
-> `tools/il2cpp_out/` (dump.cs with field offsets and RVAs, script.json, il2cpp.h).
Ghidra 12.2 (`tools/dl_ghidra`, JDK 25 already installed) headless, no window, no
auto-analysis: `pr/tools/ghidra/Il2CppDecompile.java` labels every method from
script.json and decompiles only the methods matching a pattern file
(`battle_rules.txt`, `commands.txt`) into `tools/decomp/<name>/<Class>.c`:

    tools\dl_ghidra\ghidra_12.2_PUBLIC\support\analyzeHeadless.bat tools\ghidra_proj FF6PR
      -process GameAssembly.dll -noanalysis -scriptPath pr\tools\ghidra
      -postScript Il2CppDecompile.java tools\il2cpp_out\script.json <patterns.txt> tools\decomp\<name>

(first time `-import <GameAssembly.dll>` instead of `-process`). Takes ~5 minutes.
No cross-references (no auto-analysis): `pr/tools/ghidra/callers.py <rva>...` finds a
method's direct callers by scanning the DLL's "il2cpp" code section for call/jmp rel32
(2026-11-06: the grip's ConfirmedAttack; 2026-11-07: the counter reset's InitFinishingState).
Pattern files since: twohand.txt, leap_mimic.txt, mimic2.txt, cover2.txt (-> tools/decomp/<same name>).
Overloads: until 2026-11-12 only the first method of a name was decompiled; each overload now
gets its own section, `Class$$Method#<rva>` (autobattle2-5.txt: the auto-battle replay, target
picking, the ATB queue and the executor's stack checks).
Field offsets in the C (param_1 + 0xd4...) match dump.cs; virtual calls are vtable
slots. The code is full of debug-log string building; grep past it.

### Formula probe (in-game, automated; plugin `FormulaProbe.cs`, `tools/probe_check.py`)

2026-11-05: `probe_headless.ps1` only watches and stops the process it started (it used to
stop every "FINAL FANTASY VI" process once one had a window, which would include a game you
started yourself); if someone starts the game during a run, the run stops its own game and
leaves theirs alone, and the Magicite setting a WC test changed is put back as soon as
BepInEx has read it ("Chainloader startup complete").
`-Mods Magicite` runs with another Magicite folder for that run only (put back the same way:
check the BNW port while the game is set to Worlds Collide); `-Until "<regex>"` stops as soon
as the log has a matching line (a startup check takes ~17 s instead of the whole probe).

2026-11-07: **saves.** A run loads the player's latest save, and the shop/box checks' warps
autosaved: the PC's slot 21 (the autosave, then a save from a new game's opening) was written
by those runs before anyone noticed (the user's other slots, from 2022, untouched). Every probe
request now blocks the game's saves for its run (`FormulaProbe.PatchNoSaves`: every
`SaveSlotManager.Save` overload, a script's `AutoSave`, the bestiary's
`PictureBookSaveManager.Save` (a won battle records its monsters) and `SystemConfigManager
.Save`); a harness run through a new game, its first battle and the victory screen left the
save folder byte-identical, timestamps included.

2026-11-07: **the game harness** (`BnwPlugin/GameHarness.cs`, client `tools/game_harness.py`;
the user: "make a test harness by which you can communicate with the game efficiently and play
the first battle yourself"). `probe_headless.ps1 -Extra "harness=1" -Minutes 40 -Until
"\[probe\] done"` (in the background) starts the game headless with the plugin listening on
127.0.0.1:47811: one command per line (`state`, `party`, `newgame`, `load`, `skipmsg on|off`,
`press <key> [hold frames]`, `wait <frames>`, `quit`), presses answering the game's own input
checks (`InputListener.GetKey/GetKeyDown/GetKeyUp`, `AnyKey*`). `state` gives the scene, map,
the Unity scenes, the battle's phase (`BattleController` state machine) and units, and the
running script; `party` every owned character's abilities (known / below 100%) and the party
magic list. The client's `--until REGEX` repeats a command (with `--repeat` a press between
checks) until a reply matches. The first battle from the title: `skipmsg on`, `newgame` (sent
until the title takes it: an idle title starts its attract demo), wait for `scene other` (the
opening ends on a controls help popup: `press start` closes it), `press up 90` until `scene
battle` (Narshe's guards), `press action` until `battle phase WinResult`, then until `scene
field`: about 6 minutes, nearly all of it the opening's scenes.

Later the same day (the Whelk, the event fights): `warp <map> [point]` (as the ChangeMap
instruction goes), `script <name>` (an event script of the current map run as the field runs
one, its Encount a real battle; refused while another script runs: an arrival script, or the
opening's, which may also carry the party back after a warp, so check `state` and warp again),
`slots`, `log` (the battle log since the last call: every action as it's built, actor, ability
ids, command, targets with HP, counters marked, and everyone's HP after each act ends; also in
the BepInEx log as `[harness]`). `encount <party>` sets a battle up but its loop never runs:
not for play. The Whelk from a new game: `newgame` while the title shows (`press action` until
`scene title` first if its demo started), `press start` only while `scene other` (the opening's
popups: extra presses skip cutscene pieces), then `warp 323 1`, wait until no script runs,
`script sc_e_0011`. Aiming: `press action` opens the command, a second picks the first entry,
`press right` there moves along the list, not between targets (aiming at a chosen monster isn't
worked out yet). `load <slot>` from the title follows the title's Load window
(SaveSlotManager.LoadAsync, then LoadData.RestoreSaveData, MapLoadProcessor.Start, the title's
SubSceneLoadingWait: decompiled in tools/decomp/load) but stalls before the field appears
(`scene other`); GotoLoadSaveData is the in-game load and does nothing at the title, so the
earlier runs that "loaded" a save were in the title demo. One `probe_headless.ps1` run at a
time: a run finishing removes steam_appid.txt, and a game started meanwhile is relaunched by
Steam outside the run, without its request (no save block). The script now waits for a run
still finishing (BepInEx\probe_run.lock), and stops any game started since it began when its
own exits at once.

2026-11-07: `-Extra "shopcheck=<map id>:<point>:<script>"` checks a script the port added
instead of probing: after the loaded save's field settles (messages pass at once; an event
scene's own map change is waited out, the warp retried up to 3 times), it warps to the map,
finds the script the way the field does, logs the mod's NPCs there (script ids from 9901) and
runs it; the shop screen must open. `boxcheck=` (same fields) runs a box's script and logs the
battle's monsters. A script reservation the save's event scene left (the opening's
`sc_e_0001_1`) is cancelled first: a box's battle reserves its own, and a second one throws
"Reserved script is already booked". About 2 minutes a run.

2026-11-05 (later): the probe now forces every unit to the front row while it measures
(postfixes on `CalcControllerProvider.GetCorpsId`, which the damage calc reads, and
`BattleUtility.GetCorpsId`): a back-row character in the latest save had halved every Fight
case both ways (41 "failures"). Back to vanilla 5 / BNW 5 failures (the Meltdown quirk).

The user won't play test battles for every damage formula, so the plugin tests them itself:

1. `powershell -NoProfile -ExecutionPolicy Bypass -File pr\tools\probe_headless.ps1`
   (about 4 minutes): writes `BepInEx/bnw_probe.request`, adds `steam_appid.txt` (else
   Steamworks relaunches the game through Steam, in a window), starts the game with
   `-batchmode -nographics` (no window; the plugin mutes SEAD and Unity audio), kills it if
   any window appears or after 15 min, removes what it added and runs the checker.
   **Never launch the game with a visible window on the user's PC** (2026-11-05: the user
   objected to a self-closing windowed run) and only run this with the user's OK. The
   request alone (`party`, `weapon`, `samples`, `quit` lines) also runs on the user's own
   next launch; it is deleted at start (one run only). Runs 2-5 (2026-11-05): 1.5-3 M
   calls each, 0 errors, every stat getter patched, no window.
2. The plugin loads the latest save from the title screen (`SaveSlotManager
   .GetLatestSaveSlotData` + `GotoLoadSaveData`), starts formation 7 (one Leaf Bunny) from
   the field like the boss test, and in battle runs every ability whose function group has
   a damage/heal function type (1, 2, 3, 13, 23, 32-34, 36, 41, 48, 50, 55, 56, 65, 68, 70,
   74, 76, 77; 839 abilities) through the game's own objects:
   `ParameterActFunctionManagment.GetFunctionBaseCalc(type, v1, v2, v3, SingleEnemy)` +
   `FunctionBase.MainCalc(attacker, target, ability, null)` + `GetCalcResult()`, after
   `CalcControllerProvider.SetCalcInfo` (weapon, rows, command). Both directions
   (character -> monster, monster -> character), 79 stat cases (base, one stat at a time,
   Level x Vigor/BP/Magic grids, rows), 8 samples each.
3. Stats are set by first-priority postfixes on the Confirmed* getters of
   PlayerCharacterParameter and EnemyCharacterParameter that answer only during a probe
   call (`FormulaProbe.Active`; the EL/esper/relic hooks stand aside then, elements are
   neutral). The values the getters actually returned are written next to each result,
   so a getter that couldn't be patched shows up. BNW's damage hooks stay on: the
   numbers are what a player gets. Each case runs twice: with BNW's battle rules off
   (`raw_*`: the game's own values, the vanilla baseline for every port) and on (final).
   The game caches a character's Battle Power for its Fight formula (fitted ~107 when the
   probe sets 100) and takes rows from the units, not CalcInfo, so neither is varied.
   Units are healed between frames; the run is spread over frames (150 ms each), then
   `BepInEx/bnw_probe.csv` is written and the game quits.
4. `tools/probe_check.py` checks two rule sets and writes `pr/probe_report.md`:
   **vanilla** (SNES FF6's formulas vs the raw pass: where the PR itself differs from the
   SNES; the baseline for Worlds Collide, Beyond Chaos...) and **bnw** (c2.asm formulas vs
   the final pass), each pass / fail (game vs expected per case) / no hits; defense met
   (measured from the Def/MDef 0 vs 255 cases) against each ROM's spell flags; and the
   unchecked function types with which stats move them. Exit 1 on failures. Run 5: vanilla
   534 pass, 4 fail (Meltdown 1350/1524 hit ~8x the formula in the PR).

## Deploying (pr/tools/deploy.sh) — PC and Steam Deck

`pr/tools/deploy.sh` builds + installs the plugin on the PC (`dotnet build
-p:Deploy=true`), mirrors `mods/BraveNewWorld` into the PC's Magicite folder, then
pushes the plugin, `BepInEx/config` and the Magicite folder to the Steam Deck over
`ssh deck` (tar). The first push (or `--full`) also sends BepInEx itself (core,
interop, cache, Magicite plugin), the `dotnet` runtime and the loader
(`winhttp.dll`, `doorstop_config.ini`). `--pc` / `--deck` do one side, `--deck-log`
prints the Deck's BepInEx log. Both games must be closed. Deck: same game build as
the PC (GameAssembly.dll md5 872278b3...), install at
`/home/deck/.local/share/Steam/steamapps/common/FINAL FANTASY VI PR`, app 1173820;
BepInEx under Proton needs Wine to prefer the game's winhttp.dll: deploy.sh runs
`tools/deck_winhttp_override.py` on the Deck, which adds `"winhttp"="native,builtin"`
to the existing `[Software\Wine\DllOverrides]` section of FF6's prefix
(`compatdata/1173820/pfx/user.reg`, backup `user.reg.bak-before-bnw`). Same effect as
the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`, without touching Steam's
localconfig.vdf (Steam rewrites it while running). Gotcha: backslashes in inline
`ssh deck '...'` commands get halved on the way from Git Bash, so remote edits go
through script files piped to `python3 -`.

**One mod per install (2026-11-07, the user: "WC/BNW need to be separate mods entirely").**
Brave New World and Worlds Collide are separate mods; neither plugin knows the other
(plugin/Shared holds only `MagiciteDir`: where Magicite's import folder is, so each plugin
checks its own data is installed). `deploy.sh --bnw` / `--wc` (default: the one installed
now) installs that mod (`BepInEx/plugins/<Mod>` + `StreamingAssets/Magicite/<Mod>`, Magicite's
import directory the standard `%StreamingAssets%/Magicite`) and moves the other mod's plugin
folder to `BepInEx/mod-shelf/<Mod>` (BepInEx loads nothing there): Worlds Collide's `seeds/`
stay with it and come back with `--wc`. The old shared layout (`MagiciteWC`,
`MagiciteVanilla`, the in-game Mods screen and its mode switch) is folded in on a machine's
first run. For WC, deploy.sh re-exports `wc_flags.json`, builds `plugin/WcPlugin`, runs
`tools/wc_bundle.py` and mirrors its `generator/` and `baseline/` into the plugin folder and
its Magicite folder into `Magicite/WorldsCollide`; the seed sync between PC and Deck runs only
with WC installed. Players get one zip per mod: `tools/package_mod.py bnw|wc` ->
`build/dist/<Mod>-PR.zip` (the plugin + its Magicite folder + a README; never a ROM, never the
game's own files: WC's are made on the player's machine, "WC without the game's files").
The headless runs check the mod they need is installed (the probe and the harness: BNW;
`-WcTest` / `-Live`: WC) instead of switching Magicite's folder per run.

## Plugin (pr/plugin/BnwPlugin)

`dotnet build -c Release -p:Deploy=true` (game closed) builds `BraveNewWorld.PR.dll`
and copies it plus `mods/BraveNewWorld/bnw_data.json` into
`BepInEx/plugins/BraveNewWorld/`. `MasterManager.Instance.GetData<T>(id)` reads live
master data; Harmony can't patch `MasterManager` methods (generic base) but patches
ordinary game methods fine. API dump: `vanilla/Assembly-CSharp.api.txt` (`tools/TypeDump`).
Switches in `BepInEx/config/ff6mod.bravenewworld.cfg` ([Espers] RestrictEquip,
EsperLevels, EquipBonuses, DisableLevelUpBonus, AutoSpendEL, SpendKey).

Hooking rule (learned the hard way, v0.6.0 crashed the game at startup): the game's
C++ is linked with identical-function folding, so tiny methods (empty, or one call)
share one machine-code body with many others. Hooking StatusDetailsController.UpdateView
(empty) made the hook run for unrelated objects; reading their "fields" was an access
violation, which .NET cannot catch, so the game closed. Now (v0.6.1): `EsperHooks.Patch`
reads each target's first code bytes and refuses ret/jump-only methods (logged with the
bytes), and every hook checks `EsperHooks.IsA<T>(__instance)` (native class pointer)
before touching the object. Hook the method that does the work, not a wrapper.
v0.6.1-0.8.0 had a broken guard: `Il2CppClassPointerStore<T>.NativeClassPtr` can be 0
until the interop type's static constructor runs, so IsA rejected everything and
every guarded hook (EP/EL, stat/equip bonuses, esper menus, EL display) was silently
off. v0.8.1: `ClassOf<T>` runs the static constructor first and caches the pointer,
the check is `il2cpp_class_is_assignable_from` (as `TryCast<T>`), a startup
self-test logs "[guard] self-test", and each type logs once what it rejected.
Since 2026-11-11 (v0.22.1) these helpers are the modding kit's (`plugin/PrKit/Hooks.cs`, same code, for
every PR): BNW calls `Hooks.Patch/IsA/WarnOnce/NoteOnce/TinyNativeCode`; `EsperHooks` keeps BNW's own
self-test line. The harness (`Harness.cs`) and the probe's save block and mute (`TestRun.cs`) are the
kit's too; BNW's `GameHarness` adds party/skipmsg/quit. Probe runs now log "[test] saves blocked ..." and
"[test] save to slot N skipped (test run)" instead of the "[probe] ..." lines.

Layout: `plugin/Rules/` = BNW rules with no game types (shared by the plugin and the
tests), `plugin/BnwPlugin/` = hooks, `plugin/Rules.Tests/` = offline test runner
(`cd pr/plugin/Rules.Tests && dotnet run -c Release`, 21 tests; it checks every
decoded EL/equip bonus against BNW's own bonus text).

Data: `tools/bnw_plugin_data.py` reads the BNW ROM and writes `bnw_data.json` in PR
terms: EP chart (ED/8BCA), EL bonus table (C2/614E), equip bonuses (C0/D690, 10
bytes/esper), esper users (C3/F0F5) as character_status ids, esper content ids.
The repo asm differs from the 3.0 release in places (e.g. ELTable), so the ROM wins.
The porter also writes `MSG_BNW_ESPER_BONUS_nn` text ("EL: +2 Vigor   Equip: Halves
Bolt damage") for the esper screen's bonus line.

Esper system (v0.8.4, `EsperHooks.cs`, `EsperMenu.cs`, `EsperList.cs`, `EsperStore.cs`):
- Restrictions: on the esper list (KeyInput `MagicStoneListController.ContentUpdate`
  + `MagicStoneListContentController.SetFocus`) espers the character can't use are
  greyed out (`IconTextView.SetTextColor`), as in BNW; equipping one
  (`MagicStoneDetailsController.EquipMagicStone(target, list, bool)`) plays the
  buzzer and does nothing; `OwnedCharacterClient.SetMagicStone` refuses it as a
  backstop. (v0.3.0 hid them as "not owned" instead: that left blank cells and a
  stray check mark when someone else had the esper equipped.)
- Vanilla level-up bonus off: `MagicStoneData.GetParameterType` → `Non`,
  `GetParameterValue` → 0; `GetMagicBonusMessageId` → BNW bonus text key.
- EP/EL: postfix `BattleResultProvider.Genelate`. For each character that got XP
  with an esper equipped: SP bank += SP (cap 30), EP += XP/8 × SP (cap 50,000; the
  SNES keeps 16 bits, we saturate), EL up per EP chart (cap 25, Terra..Gau only).
  SP = max(result.GetMp, result.GetAbp) (logged once to confirm which one).
  New ELs go to the EL bank (AutoSpendEL=true spends them at once on the equipped
  esper instead). Stat gains stop at 128 (base + spent), as in BNW's AddEL.
- EL bank menu (`EsperMenu.cs`, KeyInput UI = keyboard/gamepad, not touch), like
  BNW's "cursor on the esper bonus, press A". The details screen's bonus row is
  `levelUpText` ("At level up:") + `parameterValueText` (our MSG_BNW_ESPER_BONUS line).
  The label becomes `Unspent EL: 8` (BNW's wording), gold while there are ELs to
  spend; the description box on that row also gives `EL 10, next EL in 1,234 EP.` (A coloured badge behind it was tried and dropped: the bonus row's parent
  lays its children out in a row, so any added sibling becomes a layout item and
  pushes the texts sideways. Anything added there must be a child of the label.)
  Navigation uses the game's own keys, so it follows rebinding on keyboard and
  gamepad: `WaitInputUpdate` prefix reads Up/Down/Action itself; Down from the last
  spell row (`Contents` rows with AbilityData) moves the game's hand `Cursor` onto
  an anchor (child of the label) in the spell rows' cursor column on the label's
  line. The hand follows a target through a PositionConstraint, so moving its
  transform does nothing: `Cursor.SetSource(anchor)` retargets it (and
  `SetSource(row.CursorParent)` puts it back). The description box shows
  `EsperRules.SpendHint`; Confirm spends (`ShopAccounting` / `BeepId`); Up goes
  back (`SelectAbilityDataContent`).
  While on the row, Up/Down/Action are hidden from the screen via postfixes on
  `InputListener.GetKeyDown` and `GetKey` (only during `WaitInputUpdate`). Leaves the
  row if the game moves its cursor or redraws for another esper.
  Seen in the user's log: details screen polls Up/Down/Left/Right/Action/Shortcut/
  Cancel; keyboard defaults Action=Return, Cancel=Backspace, Menu=Tab, Shortcut=Q,
  Start=Space, Select=Home, PageUp=Z, PageDown=X, L/R=2/3 (the screen's Shortcut/Q
  opens the magicite list). Rect positions read in the screen's first frame are
  stale (it slides in). Optional extra key: config SpendKey (default None).
- Esper list (`EsperList.cs`): see Restrictions above.
- Saved in the save file: `DataStorage.global` was the first idea but has only 16
  slots (checked in game). Instead the block lives in FF2's keyword list
  (`UserDataManager.OwnedKeyWaordList`, `List<int>`, saved as `ownedKeyWaordList`;
  FF6 never uses it): marker "BNW1" + 3 packed ints per character_status id 1..14
  (EP/EL/EL bank/SP bank; spent HP/MP; spent stats). Other entries are left alone.
  The game computes stats on several threads (the Deck's log showed a corrupted
  Dictionary in the MaxHp/MaxMp hooks): EsperStore and the one-time log sets are
  locked since v0.8.4.
  Not yet confirmed: that the list survives save -> load (log says "started BNW
  esper data in this save" the first time a save gets a block).
- Stats: spent EL totals + equip bonus added in postfixes on
  `PlayerCharacterParameter.Confirmed*` (MaxHp/MaxMp incl. HP/MP %, Power,
  Agility, Vitality, Magic, Defense, AbilityDefense, EvasionRate,
  AbilityEvasionRate). Nothing is written into the game's own stat fields, so
  turning the plugin off reverts the bonuses.
- Elements/statuses: `ConfirmedResistanceAttributeList` gets half (type 2; cancels
  weak, never downgrades null/absorb), `ConfirmedResistanceCondtionList` gets the
  blocked conditions (value 1), `GetEquipmentConfirmedConditionList` gets the
  auto-statuses (Haste 21, Shell 22, Safe 23, Regen 20, Reflect 92). The plugin logs
  one sample of each dictionary to confirm the value meanings.
- Checked in game (auto-closed launches + the user's own session): plugin loads,
  hooks attach, bonus text shows on the esper screen, the keyword-list store is
  reachable, esper restriction applies to the list. Not yet seen: battle EP/EL,
  save/load of the block, stat display, the grey-out and spend key in v0.3.1.

SP bank (v0.9.0; `SpellBank.cs`, `EsperMenu.cs`, rules `EsperRules.TryLearn`):
- BNW (c2.asm Add_EP, c3.asm Pressed_A): each battle's SP (capped 30) is banked for
  a character with an esper equipped, besides normal learning. On the esper screen,
  A on a spell row pays the spell's full SP cost from the bank (partial progress
  doesn't count) and learns it, if the character may use that esper (it needn't be
  equipped) and doesn't know it; else buzz. Only esper data slots 0-2 hold spells
  (3-4 hold unrelated bytes); costs 1..30, in bnw_data.json espers[].spells
  (`ability_id` = PR ability).
- PR screen: Confirm on any spell row equips, and the cursor opens on spell 1, so
  BNW's layout is rebuilt: the esper's name is a cursor stop and where the screen
  opens (Confirm = the game's equip/remove), spell rows take Confirm to learn, the
  bonus row spends ELs. Up/Down move between them (the game's keys, so rebinding and
  gamepads work); the description box says what Confirm does on each stop.
- Learning: the character's `OwnedAbility` (added with
  `OwnedCharacterClient.AddAbility` if battles never started it) gets SkillLevel 100;
  the row is refreshed with `SetData(target, new MagicStoneAbilityData(...))`.
- BNW learns nothing in battle (c2.asm "Remove learning spells from post-combat
  routine"): the bank is the only way. Profile flag `spell_bank_only` (BNW) makes the
  porter write learn rate 0 for esper spells (learning.csv value2), so the PR's battle
  learning never advances; natural magic (type 1 rows) is untouched.
- Display (v0.9.1), BNW's rows (c3.asm MPCost/SPCost, "skip drawing spell progress"):
  name, MP cost (`Ability.UseValue`) + "MP", SP cost + "SP" (blank once learned; the
  PR's star marks it), no colon/progress/rate; headings "" and "Learn"; "SP 20/30"
  ("SP" in the heading color, number white) in the empty 5th row slot, right edge on
  the rows' SP units, as BNW right-aligns it under "Learn". Units are copies of the
  heading text placed after each number (children of the row texts).
- v0.9.0 bug: the readout was a copy of the "Progress" heading and
  `ColorUtility.ToHtmlStringRGB` threw IndexOutOfRange (IL2CPP), so the copy kept saying
  "Progress" on top of the real one. No ColorUtility/rich text since.
- Seen in game (Deck, v0.9.0): Locke bought Fire 2 for 15 SP (20 -> 5), skill level
  100. v0.9.1 layout checked by the user ("sp seems fine"). The help bar still says
  Equip/Remove on spell rows.

Test kit (`TestKitHotkey.cs`, rules in `Rules/TestKit.cs`; config [Testing]
TestKit, off by default): F8 with a save loaded adds every BNW esper
(`OwnedItemClient.AddMagicStone`), sets Terra..Gau to at least EL 10 with 10 ELs
banked, EP one short of the next EL and 20 SP banked (never lowers anything), and
tops gil up to 500,000 (`AddGil`). The player then saves to a spare slot: the game
writes the save itself, so no hand-made (encrypted) save files.
Controller (Steam Deck has no F-keys, v0.8.5): click both sticks in, L3 + R3. Tap in
battle = F10, tap on the field = F9, hold 1.5 s on the field = F8 (fires on release
for taps). Read through the game's own `InputListener.GetKey(Key.StickL/StickR,
GamePad)` or Unity's `JoystickButton8/9` (Xbox layout, what Proton shows the game);
the log notes once which one saw it ("[test kit] L3+R3 seen (...)").

Where SNES BNW shows EL (c3.asm): total EL beside LV ("LV 35  EL 10") in every
character header drawn by C3/0C81 (main menu party, party select C3/797D, status,
skills menus; only after meeting Ramuh, never for Gogo+); "Next EL" + EP needed
under "Next LV" on the status screen (EPUpTxt); "Unspent EL: NN" under the esper
bonus on the esper screen (Unspent_EL, C3/5A3B); banked SP beside the esper's spells.

PR version (`EsperLevelDisplay.cs`, v0.6.1): "EL n" after the level number in the
main-menu party panels (KeyInput `StatusWindowContentController.SetData`) and the
ability/status character header (`AbilityCharaStatusController.UpdateView`); the two
texts are `Instantiate` copies of the level text (font/outline match), "EL" restyled
like the "LV" label, kept as children of the level text so no layout group moves.
Status screen (`StatusDetailsController.SetParameter` / `SetMagicStoneBouns`): the
"Level Up Bonus" row becomes "Next EL" + EP needed (0 at max; blank for Gogo+).
Shown once the party owns an esper (PR stand-in for BNW's "met Ramuh" bit).
Geometry and the status screen's texts are logged once. Not yet seen in game; other
LV displays (party select, save/load, shops) not done.

Esper TODO: SP bank spell buying (data port still uses
rate = 100/SP passive learning), Alexandr/Ragnarok "+25% output" (PR hard-codes
Earring/Atlas Armlet by item id: needs a damage-formula hook), EL/EP display,
"EL gained" battle message, unequip disallowed espers in old saves.

## HD-2D plugin (pr/hd2d, pr/plugin/Hd2dPlugin)

Separate from BNW: an Octopath-style look (tilt-shift, bloom, grade, vignette) applied to
the main-game image before the game composites it with the UI. Shaders need Unity
2019.4.26f1 (installed 2026-11-04). `deploy.sh` installs it on the PC and the Deck. See
`pr/hd2d/README.md`.

## Worlds Collide port design (user, 2026-11-05)

The user wants WC "with options WITHIN THE GAME that let you change seed stuff as if you
were on the website", and new seeds **without restarts** (a restart-to-apply design was
rejected). Beyond Chaos and T-Edition are dropped.

- Source: `mods/worlds-collide/` (WC 1.4.3, Python, 435 files / ~50k lines, flags parsed by
  `args/`, flag metadata for a picker via `build-wc-flag-metadata.py`, presets in
  `mods/worlds-collide-presets.json`). WC turns vanilla ROM + flags + seed into a ROM.
- Generation in game: an options screen in the plugin (flag groups from WC's metadata,
  seed field, presets) runs the bundled WC generator and our porter through an embedded
  Python (Windows embeddable runtime; under Proton on the Deck it runs as a Windows
  process too) on the user's vanilla ROM, giving the same PR data our porter writes to
  `mods/WorldsCollide` (master CSV rows, messages, maps, AI scripts).
- Applying it live, no restart: master tables are `MasterManager.GetList<T>()`
  dictionaries; each changed row is rebuilt with the master type's `(string masterLine)`
  constructor (columns in the game's order, as our CSVs are) and put in place. Messages go
  into the MessageManager tables; map entity changes (chests) and AI scripts are swapped
  where they're loaded (hooks on map / monster AI loading). The seed and flags are stored
  with the save (plugin save data), so loading a save re-applies its seed.
- Done (2026-11-05): `tools/wc_seed.py --seed S --flags F [--preset P] --out DIR` runs
  WC's own wc.py (fresh Python process, seeds match the website; needs the US v1.0 ROM,
  `rom/vanilla/Final Fantasy III (USA).sfc` = WC's sha256) and our porter: **~4 s** for a
  seed (2188 rows in 16 tables + 401 map/AI files for Moogle Cup). `LiveData.cs` applies such
  a folder to the running game: every master row rebuilt with the type's own
  `CreateMaster(line)` (MasterBase.LineParse, positional columns) and set in
  `MasterManager.GetList<T>()[id]`; messages via MessageManager (headless test: 2188 rows
  applied, monster 1 HP 50 -> 40, item 1 price 500 -> 54, Terra Vigor 30 -> 33, no
  restart). `MasterBase.Parse<T>(csv)` returned nothing for our CSVs (unused).
  Magicite (strings in Magicite.dll: TextAsset_bytesPatch, ResourceManager_CheckCompleteAsset,
  ReadAllText) serves mod files when the game loads each asset, so per-seed map/AI files
  can be overwritten in the active Magicite folder (files must exist at startup).
- Done (2026-11-05, WcPlugin v0.1.0): the in-game screen, generation and live apply, see
  "Worlds Collide in game" below. Headless tests pass (two seeds made in game in 4-9 s and
  applied live, rows and files checked, vanilla restored, a save loaded through the hook).
- What data can't carry: WC's open world, objectives and recruitment are event-script
  rewrites (WC `event/`): they need PR event-script edits per map (PR events are
  per-map JSON scripts) plus plugin logic (objective tracking, unlock conditions).
  That is the bulk of the WC port.

## Worlds Collide in game (pr/plugin/WcPlugin, 2026-11-05)

How a player uses it: the title screen's menu has an entry "Mods" ("Worlds Collide" in WC
mode), between Extras and Options (F7 anywhere also opens it) -> Mod: Worlds Collide ->
restart once. (2026-11-05: the first version used a note in the screen's corner and the
Select button; the user found the note jarring, and Select isn't bound on the Deck.) Then on the title
screen: Seed page = preset, seed (type it, or left/right for a random one), the flag string
(edit as text, paste from the website with Ctrl+V or "Paste flags from clipboard"),
"Generate and play this seed"; pages 2-30 = every WC flag by WC's own sections, dropdowns
as on the website (L/R to change page). A few seconds later the seed is live: New Game.
Saves remember their seed; loading one brings its seed back. Earlier seeds are listed and
can be switched to at the title screen; "Play without a seed" = the game's own data.

- Modes (`plugin/Shared/ModMode.cs`): Magicite loads every folder in its import directory
  (`silvris.magicite.cfg` "Import Directory"), so each mode has its own: BNW =
  `StreamingAssets/Magicite`, WC = `StreamingAssets/MagiciteWC`, vanilla =
  `StreamingAssets/MagiciteVanilla`. Switching rewrites that line (takes effect at the next
  start). The BNW plugin turns itself off unless `BraveNewWorld` is in the import directory
  (the formula probe still runs in any mode).
- Flags (`tools/wc_flags_export.py` -> `plugin/WcPlugin/wc_flags.json`): read from WC's own
  argparse (sections, types, ranges, choices, nargs, help), argparse's mutually exclusive
  groups as dropdown rows (WC's own metadata script drops them), labels from long option
  names (+ a few hand names). 253 flags, 29 sections, 28 dropdowns. Model: `WcFlags.cs`
  (no Unity): parse/serialize flag strings, steps, defaults; unknown tokens kept as typed.
  `plugin/Wc.Tests` (dotnet run) + `tools/wc_flags_check.py`: presets round-trip identically
  and every flag the screen can turn on is accepted by WC's parser (442 strings, 0 problems).
- Generation (`WcGenerator.cs` -> `tools/wc_seed.py`, background process, no window): WC
  (fresh Python, its own code) on the vanilla ROM, then our porter; the seed folder
  (`plugins/WorldsCollide/seeds/<date>-<seed>/`) holds FULL tables for every table the
  porter can write (`wc_live.MANAGED_TABLES`, 20) and the full system_en text, so applying a
  seed also undoes the previous one, plus the changed chest maps / AI scripts (`files/`),
  WC's log (`wc_log.txt`: flags, spoilers with -sl) and `wc_seed.json`. ~4 MB, ~4 s.
- Bundle (`tools/wc_bundle.py`, deploy.sh runs it): `generator/` = CPython 3.12 copied from
  this PC's install (26 MB; no download), WC 1.4.3, the porter, the PR data it reads (20 MB:
  master, message, monster_ai, battle_script, battleextradata, assetspath, every map's
  entity_default.json; traced with an audit hook), the ROM, BNW's font table
  (`data/vwf.tbl`; `bnw_text` also falls back to stored table counts without BNW's asm).
  `--check` makes a seed with the bundle alone. Same Windows Python runs under Proton.
- Live apply (`WcSeeds.cs`, `Shared/LiveData.cs`): rows via each master type's
  `CreateMaster(line)` into `MasterManager.GetList<T>()`, skipping rows already set to the
  same line (first seed 15,638 rows in 0.17 s; switching seeds ~1,600 rows in 0.03 s); text
  into MessageManager, with each literal `\n` turned into `Environment.NewLine` first, as the
  game's own `MessageManager.Parse` does (seen in its disassembly; before 2026-11-12 the live
  apply skipped it and a seed showed "\n" in menu texts such as MSG_SYSTEM_183). Files: Magicite only serves files that existed at startup, so the
  WC folder ships every file a seed can change (`wc_live.write_baseline`: 156 chest maps +
  the 384 `sc_ai_bnw_NNN` AI slots, 3.8 MB) and a seed overwrites contents; the previous
  seed's files go back to `baseline/files`. Cached assets for swapped files are dropped from
  `ResourceManager.completeAssetDic`. At startup the folder is synced to the active seed
  (deploy may have mirrored the baseline over it) and its rows/text applied once data loads.
- Starting gil/items/espers (2026-11-05): `wc_seed.py` reads them from the WC ROM's start
  routines (event code called from 0xC9A4F: 84 add GP, 80 / B0 n 80 B1 add items, 86 add
  esper), as PR content ids in `wc_seed.json` "start"; the plugin grants them once on a New
  Game (leaving the title without loading a save, then the first map: WcSeeds.GrantStart).
  Headless test: granted to a loaded save in memory, gil +5000 and the 10 items.
- Reward multipliers (`WcRewards.cs`): -xpm / -gpm scale EXP and gil in
  BattleResultProvider.Genelate (prefix), -mpm the magic points in BattleAddExpPlug.GetAp
  (postfix, a 128-byte method of its own); from the active seed's flags. -nxppd (2026-11-06): the total times the characters it is split between.
- Steal flags (`WcSteal.cs`, WC data/steal.py): -sch Level + 90 - target Level vs
  random(0..99) (fails at <= 0, sure at 128+), rare 3/8; -sca always steals, rare 3/8 when
  both. Thief Glove doubling not yet.
- Enemy scaling (`WcScaling.cs`, WC battle/scaling*.py): once a battle's monsters are up,
  level = count x F (time: minutes / F) clamped to [3, -msl], + random(-1..2) per monster;
  HP/MP = (value / master level) x HP/MP scale level; EXP/gil the same in
  BattleAddExpPlug.GetExp/GetGil. Counts: party average / highest level, characters (main
  14 owned) + espers owned, play time; dragons/bosses defeated and WC checks aren't tracked
  (0) yet. Dragons and final battles skip scaling unless -sed/-sfb (by formation, or by
  their monsters: BattlePlugManager.MonsterParty can stay null). Headless battle: Leaf
  Bunny Lv 5 / 33 HP -> Lv 3 / 18 HP under Moogle Cup's -lsced/-hmce 2.
- Saves: postfix on `SaveSlotManager.Save(int, ...)` records slot -> seed
  (`seeds/state.json`); prefix on `GotoLoadSaveData(SaveSlotData)` re-applies that slot's
  seed before the load (works, headless test). Saves made in other modes drop the mapping.
- Title menu entry (`WcTitleMenu.cs`, decompiled `tools/decomp/title`): KeyInput
  TitleMenuCommandController.Initialize(contents) puts ids < 6 in the main menu (6-11 Extra,
  12-15 Options, rest Config); picking one calls TitleWindowController.SetCommand(id, ...),
  which ignores unknown ids. We add TitleCommandContentData(id -1) before Option, set its
  label after the menu is built (its Name isn't found by the menu's text lookup), and
  SetCommand(-1) opens the screen; the Confirm press that picked it is ignored by the screen.
  Headless test: entry present, label set, choosing it opens the screen.
  2026-11-05 fix (user: "overlapping ... or pushing the menu down"): the main menu is a
  VerticalLayoutGroup (spacing 12, centred) in a fixed 313-px box sized for the game's 5 rows
  of 53; a 6th made it 378 px, ~32 px past both edges. With ours present the gaps shrink to fit
  the same box (6 rows: spacing 0, 318 px). `-WcTest "menu=1"` runs only the menu checks and
  logs the layout ("[layout]" lines; the menu is switched on just for the layout pass).
- Screen (`WcScreen.cs`): IMGUI, but this build stripped GUILayout's fields and GUI.TextField
  / Toggle (GUI.Box/Label/Button, Event, GUIStyle colours/sizes, systemCopyBuffer survive), so
  rows, cursor and text entry are our own. Input through the game's `InputListener` (its key
  config), which is switched off for the game while the screen is open (prefix on
  GetKey/GetKeyDown/GetKeyUp/AnyKey*; the screen reads with a bypass); text typed through IMGUI
  key events (the game uses the new Input System, so no legacy Input). 2026-11-05 (user: not
  navigable with a controller on the Deck): pads reach the game through Input System actions
  (KeyConfig.SetGamePadKey / InputSystemManager), and opening the screen switched the whole
  EventSystem off; now only the title canvases' GraphicRaycasters are paused (no clicks through)
  and the screen also reads `UnityEngine.InputSystem.Gamepad.current` directly (D-pad/left
  stick, A south = confirm, B east = cancel, LB/RB pages). Look: the game's own font (from its
  UI Text), FF6-style windows (blue gradient, light rounded border, 9-sliced generated
  texture), drop-shadowed text. Not seen on a screen yet (headless can't draw).
  Page list (user: "how am i supposed to navigate the left bar on controller?"): the cursor can
  sit on the page list: B from the rows (or Left on a row without a value) goes there, Up/Down
  picks a page, A/Right goes into it, B there closes; L1/R1 still step pages. Headless flow
  test (WcScreen.NavTest): "B:list p0 Down:list p1 A:rows p1 RB:rows p2 B:list p2 B:closed".
  The direct pad read switches itself off once the game's own check reports a pad press (both
  seeing one press on different frames would count it twice).
- One gamepad (`PadFix.cs`, every mod mode, config [Input] SingleGamepad; user, 2026-11-05:
  double presses on the controller in the Pixel Remaster itself, on the Deck): the game reads
  pads through Input System actions bound to any gamepad, and under Proton one controller can be
  two devices (Steam Input's XInput pad + the raw HID one). Every input device is logged ("[pad]
  input devices"); with 2+ gamepads one is kept (XInput layout first) and the rest disabled.
  Headless has no devices. Deck log (2026-11-05): ONE gamepad ("Xbox Controller",
  XInputControllerWindows, iface XInput) + Keyboard + Mouse (RawInput): not two devices.
  The game's pad layer (decompiled `tools/decomp/inputsys`, InputSystemManager.Update): per
  action a state 0 idle -> 1 down (value > 0.5) -> 2 held -> 3 up -> 0, stepped once a frame;
  GetKeyDown = state 1 (buttons or axes); only a Switch Pro processor, Xbox pads use the action
  fallback. Settings: Input System ProcessEventsInDynamicUpdate, XInput polled at 60 Hz, target
  60 fps, no vsync. Next: `InputDiag.cs` ("[press] f<frame>" lines, config [Input] LogPresses,
  600 lines a session) logs per frame the raw pad changes, keyboard keys, the game's action
  states and every InputListener.GetKeyDown that answered true, to see which layer adds the
  second press. Outcome (user: "the double press is gone" with the build that turned the direct
  pad read off once the game's own check sees the pad): the doubles were ours. WcRunner.Update
  can run before the game's InputSystemManager.Update in a frame, so the direct read
  (wasPressedThisFrame) saw a press one frame and the game's state machine the next: two steps
  per press on the WC screen. Now also a per-key debounce (a second down within 3 frames is
  the same press), which covers the first press before the switch-off. The press log stays in,
  off by default (LogPresses = false, PC and Deck configs). Laid out in virtual
  1280x720, drawn at the screen's size (rects and font sizes scaled, not GUI.matrix: crisp
  text at 1080p/4K). Generate/switch seeds only on the title screen. Each WC page says
  whether the PR port carries it (`wc_flags_export.PORT_STATUS`: yes / partly "*" / no
  = dimmed, with a note in the help line): flags of "no" pages stay in the seed's flag
  string but change nothing in the PR yet.
- Tests: `tools/probe_headless.ps1 -WcTest "seed=a;preset=moogle cup;seed2=b;load=1"` (no
  window, muted; points Magicite at MagiciteWC for that run only and restores it): makes the
  seeds through the installed bundle, checks live rows (monster HP, item prices, character
  stats) and Magicite files against each seed, restores vanilla, checks the screen's logic
  (every page and flag row stepped/toggled: 185 rows, 925 actions), loads the latest save
  through the hook, deletes its seeds. Last run: 0 failures.
- Not verified: how the screen looks and feels (drawing needs a window; first look is the
  user's), the generator under Proton on the Deck, text in other game languages (seeds only
  carry English text).
- WC events, groundwork (2026-11-05): PR event scripts are per-map JSON (`map_*/.../sc_e_NNNN.json`,
  2017 scripts, 151k instructions) on the same mnemonic VM as the AI scripts (our
  `ai_compile.py` already emits it): 111 mnemonics, the story ones being `SetFlag` /
  `ResetFlag` / `Branch` on `ScenarioFlag1..N` (+ `MapLocalVariable`), `ChangePartySlot` /
  `OrganizePartySlot` / `BranchByCharacter` (party), `SysCall` with Japanese routine names
  (recruiting, vehicles...), `GetItem` / `RemoveItem` / `RemoveGil` / `TreasureBox`,
  `Encount` / `EncountBoss`, `ChangeMap` / `ChangeScript` / `SetEntities`, `Msg` keys
  (`E0309_00_383_a_01`). WC (`mods/worlds-collide/event/`, 62 modules) instead sets SNES
  event bits at the start (`event/start.py`: story scenarios finished, Shadow's dreams seen,
  continent floating...) and rewrites each check's SNES event code into a reward.
  Plan: (1) a SNES event bit -> PR ScenarioFlag map (match each SNES event's bit writes to
  the PR script of the same event, like idmap.py did for items); (2) per check, a PR script
  template that gives WC's reward (character / esper / item, from the seed's spoiler data,
  `-sl`) and sets the same flags; (3) those scripts written per seed as managed files
  (`wc_live.managed_files` grows by the scripts WC touches: Magicite serves them like the
  chest maps); (4) objectives and the Kefka's Tower unlock tracked by the plugin (flags +
  a menu page). Start-of-game state (party, gold, items, espers) can be set by the plugin
  at New Game before any of this. Checked 2026-11-05: the PR did NOT keep SNES event bit numbers as ScenarioFlag
  indexes (WC's 201 named bits hit used ScenarioFlag1 indexes 116 times, but shifted by
  1/3/7 they hit 116-119 times: chance); flags are spread over ScenarioFlag1 (0-368),
  ScenarioFlag2 (0-259), ScenarioFlag3 (0-79), ScenarioFlag4 (132-206), TreasureFlag1/2. The
  map needs event-by-event matching (WC's start bits first).
- WC events, first milestone done (2026-11-05): **WC's start**. New Game with a seed active
  skips the opening and starts on the Blackjack's deck (Map_20170, PR map 119) in the World of
  Balance with the airship parked outside Narshe (WoB tile 84,34: PR world tiles are the SNES
  grid, Narshe's entrance object sits at pixel 1344,544 = tile 84,33), the seed's starting
  party, and WC's gil (exactly the seed's: the PR's INIT_GIL 3000 is replaced), items and
  espers. Headless test (`-WcTest "seed=..;preset=moogle cup;newgame=1"`): party [10 13 1] =
  the seed's, gil 5000, Blackjack map 1 at 84,34, deck reached in ~4 s, 0 failures.
  How it works:
  * the event VM's `SysCall` looks names up in `External.SystemCallTable` and calls them
    (`External.Misc.SystemCall`, decompiled `tools/decomp/events`); a Harmony prefix
    (`WcEvents.cs`) takes names starting "WC:" so our scripts can call into the plugin for the
    per-seed part. `WC:start` (grants, sets MapLocalVariable 6 = 1 when a seed is active),
    `WC:party` (the seed's characters join through the game's own `メンバー加入：<name>`
    calls; status ids Terra 1 .. Umaro 14 = `Current.JoinCharacter`'s);
  * `tools/wc_events.py` writes the changed scripts (static, same for every seed) into the
    WorldsCollide Magicite folder with their asset keys (`wc_live.write_baseline`).
    `sc_e_0001` (the opening) jumps from its first instruction to an appended segment: the
    attract demo and seedless New Games go back to the vanilla code; otherwise Terra/Biggs/
    Wedge leave, `WC:party`, `SetVehicle [4, 1, 84, 34, 0, 0]` (Blackjack, WoB),
    `ChangeMap [119, 1, ..] sc_e_BJinboard_1` (the game's own boarding arrival). Appending
    and jumping keeps every vanilla jump index valid (`tools/pr_script.py`: load, dump with
    indices, append, save);
  * `tools/wc_run.py` runs WC's own `wc.main()` unchanged and records what its events chose
    (patched in through an import hook: WC claims ROM space while importing, so nothing may
    be imported before it). `wc_seed.py` stores the start party and every event's rewards in
    PR ids (`wc_seed.json` start.characters, checks: 57 events with gate character and
    rewards) for the plugin;
  * self-tests never save: the deck's arrival script autosaves, and the first New Game test
    (2026-11-05 12:53) overwrote the PC's autosave slot (21) with a test game before this guard
    existed (`WcSeeds.NoSaveInTest`).
  Not done in the start: WC's start story flags (Locke/Terra/Sabin scenarios and the Vector
  dinner finished, Shadow's dreams seen, continent floating, Banon met, Genji Glove/Gauntlet
  given). The PR renumbered SNES event bits, so each needs its PR flag found; and some must
  wait for their events (e.g. the PR's "continent risen" flag, ScenarioFlag2 28, opens the
  vanilla Floating Continent path, which ends the World of Balance). `tools/pr_flags.py`
  catalogs all 668 PR flags (which scripts set/reset/test each, with the scripts' labels and
  nearby message keys) for that matching. A WC New Game today = the seed's party and airship
  in a World of Balance whose towns are in their opening-of-the-game state.
- WC events, second milestone (2026-11-05, all headless-tested, 0 failures):
  * **The event VM's jumps** (traced in game): Jump [T], Branch [.., .., T] and Call [T] go on
    at T + 1; every vanilla target is a labelled Nop that never runs itself. So a Jump put in
    place of a labelled Nop is skipped by code that branches there (the first wheel attempt
    ran the vanilla menu). `pr_script.hook(script, at, name, ops)` now diverts the
    instruction AFTER a label: it moves into the appended block (label "vanilla", then a jump
    back), and blocks use `S.label(x)` / "@x" targets. Also found: the opening's
    advertise-demo call sets map-local 7 to 1 on a New Game (0 in the title's demo).
  * **World switch** (WC event/airship.py): the Blackjack's wheel (sc_e_BJoutboard) offers
    "(Fly to the World of Ruin.)", the Falcon's (sc_e_FAoutboard) "(Fly to the World of
    Balance.)", in a WC game (ScenarioFlag4 0, set by WC's start). `WC:to_wor` / `WC:to_wob`
    put the other airship (transportation 5 / 4) at this one's position, map 2 / 1, released
    (`SetDataStorageFlag`: TreasureFlag4 136 / 135, which the wheels test as "placed before");
    then ChangeMap to that world with the game's own flying arrival (sc_e_FAoutboard_1 /
    sc_e_BJoutboard_1). ScenarioFlag2 97 ("after obtaining the Falcon") set in the WoR and
    cleared in the WoB, as WC does with IN_WOR / GOT_FALCON. Tests: worlds=1 (Blackjack wheel ->
    map 2, Falcon at 84,34) and worlds=2 (Falcon wheel -> map 1, Blackjack where the Falcon was).
    The wheel lines are new message keys (WC Magicite folder: message/.../story_mes_en.txt +
    story_cha_en.txt with an empty speaker, keys/wc_message.json; checked loaded in game).
  * **Checks**: `WC:reward <module> [n]` gives the seed's n-th reward for that WC event
    (character: the game's join call; esper: AddMagicStone; item: AddOwnedItem) and sets the
    text "WC_REWARD" to its "Obtained ..." line (names from content.csv / MessageManager,
    characters as (TINA)-style tags); each check's done flag is ScenarioFlag4 n
    (`wc_events.CHECK_FLAGS`, saved with the game). `WC:offer` / `WC:pay` put a seed's price in
    a line and take it (map-local 0 = paid, like the game's RemoveGil, whose argument indexes
    a price list we don't need).
  * **Tzen** (WC event/tzen.py, check flag 1): the esper seller is in WoB Tzen from the start
    (sc_map_20190; the PR shows him after ScenarioFlag1 203, a big story flag, so the map script
    branches on the WC game flag instead), sells the seed's reward for the seed's random price
    in each world (WC logs both: "(23,273 WOB, 45,335 WOR)", parsed by wc_seed.py), and buying
    in one world sells out the other (both vanilla bought flags, ScenarioFlag1 206 / ScenarioFlag2
    57). Test tzen=1: warp to Tzen, buy: offer text names the price, gil 28273 -> 5000, Phoenix
    magicite received, flags set; the second talk sells nothing.
  * Test tools (WcTest / WcEvents, test runs only): `GotoMap(map, point)` warps the way the
    ChangeMap instruction does (`EventProcedure.EventGotoMap` with a `LoadData`);
    `RunScript(name, answers...)` starts a script on the current map
    (`MainGame.eventProcedure.EventRunScript`; refused in flight and right after it: the
    airship lands/boards itself); Select windows answered from a queue, Msg windows logged and
    skipped; a trace of Jump/Call/Branch/SysCall/ChangeMap... with the program counter
    (hooking `MainCore.Execute` itself broke the VM: it returns a Nullable<int>).
- WC events, third milestone (2026-11-05):
  * **Character gates** (WC -cg, on in both presets): every join in a WC game sets
    ScenarioFlag4 100 + status id (a postfix on `Current.JoinCharacter`, which every join call
    goes through); `WC:gate <module>` sets ScenarioFlag4 131 to 1 when the check's gate character
    has joined or the seed has no -cg. Joins of our own (start party, character rewards) call
    `Current.JoinCharacter(id, -1, 1, true, false, false)` directly: the named calls (JoinGau...)
    look the character up first and throw for one that never joined.
  * **Ancient Castle** (check flag 2, gate Edgar): the stone queen (sc_e_0890) gives the seed's
    reward instead of turning Odin into Raiden. WC's start bit GOT_ODIN (no Odin statue) = the
    PR's ScenarioFlag2 172, tested only in the castle, now set by WC's start (`START_FLAGS`); it is
    also what makes the queen's event appear. Test: gate closed until Edgar joined, then Gau joined
    from the statue (the seed's reward), check flag set.
  * **Gau's father's house** (check flag 3, gate Shadow; Map_20090 = PR map 73): the NPC (the PR's
    Shadow) is there in a WC game until the check is done and the gate is met (the house's map
    script), talking (sc_e_0097) gives the seed's reward and he leaves (ScenarioFlag1 71, read only
    by this house). Test: gate closed / open, Relm joined.
  * **Kohlingen** (check flag 4, gate Setzer; Map_20131_1 = PR map 98): the NPC (the PR's Shadow,
    hired there for 3000 gil: so RemoveGil [203] = 3000) is in the WoB inn until the check is done
    and the gate is met; talking gives the seed's reward for free (WC), ScenarioFlag1 147 = hired.
    The PR has no WoR copy of this scene. Test: gate closed / open, the Ragnarok sword received.
  * **The freeze in Gau's father's house** was `PlaySE [id, 1]`: the 1 keeps the sound's handle
    in a script-local variable, and a script that declares none (that house's, Kohlingen's)
    makes the game throw inside PlaySE, which hangs the VM (no log; found by wrapping PlaySE in a
    hook, whose wrapper logged the exception instead). Vanilla uses [id, 1] only in scripts that
    declare one (1784 uses, 0 without). `pr_script.append` now gives every script it changes a
    local ("なし" = none, value 0) if it has none.
  * Learned on the way: the VM sometimes runs one instruction twice in a row (traced "ran 2x"); a
    reward that joined a character twice hung the game. Every `WC:` call now ignores an immediate
    repeat of the same script line, and joins skip characters already in the party. A script
    started from a test (`EventRunScript`) can't be started again in the same game session, even
    after a warp (the interpreter isn't busy and the text is there; not found why): each test runs
    a check's scene once and checks the closed gate through `WC:gate` itself. The autosave a test
    skips now tells its caller "done" (else the caller waits forever). Messages close themselves in
    tests; a test waits for the script's main core to Exit and polls for the check flag.
- WC events, fourth milestone (2026-11-05):
  * **The PR's merged towns**: the PR keeps one map for a town's WoB and WoR states (Narshe,
    Figaro... 40 maps) and switches them on ScenarioFlag2 41 ("after the world's collapse", set
    when the world collapses). The airships' world switch now sets it in the WoR and clears it in
    the WoB, with the Falcon flag: WC's IN_WOR.
  * **Tritoch** (check flag 5, no gate; the PR's Valigarmanda on Narshe's cliffs, map 20): the
    post-battle scene (sc_e_0780_1) gives the seed's reward instead of his magicite; the rest of the
    scene as vanilla (`pr_script.copy_ops` copies vanilla instructions into the block). The cliffs
    already show him in the WoR (vanilla: after the Falcon). Test (WoR flags set, warp, the
    post-battle scene without the battle): the Genji Glove.
  * **Zozo** (check flag 6, gate Terra; the top of the tower, map 395): only Ramuh is there in a WC
    game (WC removes esper-Terra and the extra magicite: the PR's Siren / Kirin / Cait Sith pickups,
    sc_e_0304-0306, are left out), his talk as vanilla, then the seed's reward instead of his
    magicite. WC's random clock puzzle isn't ported (the PR's clock answer and its NPC hints stay
    vanilla, so they agree). Test: Cyan joined.
  * The PR's 17 "Obtained ... magicite!" scenes, mapped to WC checks for the next ports: Fenrir =
    Mobliz WoR (map 212), Lakshmi = Owzer's Mansion (446), Midgardsormr = Umaro's cave (496),
    Alexander = Doma WoR (304), Ifrit/Shiva = Magitek Factory (398), Golem/Zona Seeker = Jidoor's
    auction (111), Crusader = Kefka's Tower (551, the dragons), Bahamut = Doom Gaze.
  * Next: the **Track menu** in the field menu: `Last.UI.CommandMenuController.Initalize(List<
    CommandMenuContentData>)` builds the main menu's entries (MenuCommandId: Item ... Save) and its
    confirm handler (`<UpdateController>b__12_0`) raises OnKeyAction(id); an entry with an unused
    id caught there can open a WC screen (Checks by gate character greyed when done, from the
    ScenarioFlag4 check flags; Progress counts; the seed's flags; objectives later).
- Test runs this round (all 0 failures): newgame+worlds=1, tzen=1, check=ancient_castle (also
  with Relm forced as the reward: `reward=character 9`), check=gau_father_house (Relm),
  check=kohlingen (an item), check=tritoch, check=zozo. `tools/wc_test_reset.py` puts the install back (no seed active,
  test seeds removed) when a test is killed before its own cleanup.
- Next for WC, in order: (1) the start's story flags (above); (2) each check's event:
  replace the vanilla reward with `SysCall WC:reward <check>` (the plugin gives the seed's
  character/esper/item and sets that check's done flag), strip the cutscenes WC strips, gate
  by character like WC; done flags in a ScenarioFlag range the PR never uses (ScenarioFlag4
  0-131 has no use in vanilla scripts), so they live in the save; (3) objectives (WC's 25
  condition types, results such as the Kefka's Tower unlock) tracked by the plugin from those
  flags; (4) the **Track menu** the user asked for (WC adds "Track" to the main menu:
  Objectives with progress, Checks grouped by gating character and greyed once done,
  Progress counts (checks, characters, espers, dragons, bosses), the seed's Flags): a
  field-menu entry like the title-menu one, opening a plugin screen in the WC screen's style,
  controller-navigable; (5) WC's **hints**: the Track menu's check lists, objective texts,
  and the event hints flags turn on (e.g. `-adeh`, the auction house door NPC naming the
  esper on sale); (6) cross-machine saves: Steam Cloud carries saves between PC and Deck but
  not the seed folders or the slot records, so a save made on one machine finds no seed on
  the other; a fix is to keep the seed text + flags with the slot and regenerate the seed
  when it's missing (generation is deterministic).
- Not ported yet (the bulk of WC): its rewritten events (open world, character gating,
  recruitment, 68 reward checks, 25 objectives, Kefka's Tower unlock), and the flags that are
  assembly patches rather than data (bug fixes, challenges, -move, -warp, -scan, -npctips...).
  A seed today = WC's randomized data and AI on the vanilla story.

## Plan

1. [x] Install BepInEx + Magicite, extract vanilla PR data
2. [x] Map SNES IDs → PR IDs (characters, items, enemies, spells), verified on vanilla
3. [x] Converter v1 (`tools/port_bnw.py`) → `mods/BraveNewWorld`, installed and
       verified in game (Guard HP 50, Terra Vigor 30/Magic 42, Dagger 60)
4. [~] Port: done = characters, growth, equipment stats/prices/equip groups,
       consumable prices, enemy stats/steals/drops, spells/skills (all 255 slots:
       numbers, element, targeting, status effects, names, descriptions), esper
       spell lists (BNW: rate 0, spells only from the SP bank), Terra/Celes natural magic, Blitz/Bushido
       levels, item names/descriptions, enemy elements/immunities/initial statuses,
       armor elements/status protection/auto-status, weapon elements, formation
       AP (347), shops (83 matched, 515 rows), chests (118 in 70 map files),
       encounters (144 sets, 191 new formations), Rages (138).
       Done since: weapon procs, weapon special effects, relic effects (v0.10.0).
       Todo = enemy special attacks, 4
       relic→consumable conversions (188/208/209 are dead slots in BNW, 189 = the
       inert Gum Pod; PR still has the vanilla relics in those armor rows), 13 BNW
       chests that were empty in vanilla,
       enemy sprites (2026-11-05, `tools/sprite_compare.py`: SNES monster graphics entries
       D2/7000 compared by content (first 96 bytes of tiles at E9/7000 + palette colors at
       D2/7820; BNW shifted most pointers by 64 bytes, so raw pointers don't compare): 141 look
       exactly like vanilla, ~220 have vanilla shapes in BNW colors (some on another monster's
       shape, e.g. 26 Banshee = Critic's), 23 are new art (dragons, Dahling, the Wrexsoul and
       Kefka towers' parts...). The PR's monster_asset_id points at pre-drawn images, so only a
       shape swap is possible as data (wrong colors); recolors/new art need image work)
5. [~] Port: enemy AI — 271 regular enemies + 49 bosses (24 hybrids, untested)
       on BNW's AI; todo = the other 39 bosses (entrances/exits, events, per-boss PR routines), targeting,
       battle messages
6. [~] Port: text — 2026-11-05 groundwork: `tools/snes_dialog.py` decodes the SNES dialogue
       (3084, pointers CC/E602, two-letter pairs C0/DFA0) and `tools/dialog_align.py` ties SNES
       speaker turns to PR story keys by word overlap + in-order gap filling: 1249 of 3344 turns
       (1032 confident anchors, 217 fills), 1244 of 4293 PR lines (29%). Not enough to swap text
       (a wrong line is worse than the PR's own); next: per-scene sequence alignment (DP),
       townsfolk lines (N... keys) by map, then BNW's text for each matched turn (names as
       (TINA)-style tags, "Name:" prefixes dropped: the PR's speaker is in story_cha). A Japanese
       table for the SNES JP ROM (rom/vanilla) would allow JP-to-JP matching, far closer; no
       table is in the project (a download would need the user's OK; the user chose English only).
       Later the same day: per-scene sequence alignment (anchors locate each PR scene's window
       of SNES turns, then an in-order DP; seeds for scenes without anchors) + speaker check
       (story_cha speaker vs the SNES "NAME:" prefix, +-0.2): 2418 of 4293 PR lines (56%),
       speakers agree on 95% of pairs where both name one (most disagreements are naming:
       Mayor/Elder, Girl/Woman). Found: BNW's script is GBA-translation-based, like the PR's
       ("There's the city.", "But... I want to know now..." read the same in both), so most BNW
       lines already read close to the PR's; but of 2357 BNW-to-PR pairs, 978 differ a lot and
       sampling shows many of those are off by a line or two inside the right scene (the PR
       splits/merges speaker turns, which a 1:1 alignment can't follow). Not ported: wrong lines
       would be worse than the PR's own. Next would be page-level (<PRESS>/<P>) segmentation with
       1:n alignment, then porting only lines BNW really changed.
       Dialogue checked 2026-11-04: the PR keys story lines per event
       (`story_mes_en.txt`: E0055_00_343_a_01 = event 55, scene, an event-local number
       1..999, variant, line; 3546 lines, 234 numbers), not by SNES dialogue number
       (SNES #343 is a Banon line, PR's 343 is Vargas), and its English is a different
       translation: porting BNW's script needs fuzzy alignment per event. Low priority.
       Done = names and descriptions (spells, items, espers, bonuses);
       todo = BNW script (dialogue), battle messages
7. [~] C# plugin (BepInEx IL2CPP) for BNW mechanics, from `asm/banks`: done =
       esper restrictions (greyed list), EP/EL with EL bank + cursor row on the esper
       screen, EL beside LV and Next EL on the status screen, esper equip bonuses
       (not the two +25% output ones), vanilla level-up bonus off, F8 test kit,
       SP bank spell buying (v0.9.0).
       Confirmed in game: EP/EL after battle (v0.8.7, with boss EXP fixed), SP bank
       (v0.9.1). Unconfirmed: EL data across save/load, equip bonuses in battle.
       Weapons/relics (v0.10.0): proc odds per attacker, hard-coded item lists moved to
       BNW's items, x2 EXP/gil off. v0.10.1 (`RelicRules.cs`): two-handed x1.5
       (`ProviderManager.Instance.EquipmentAbilityProvider.serialData`
       .TwoHandedMagnification 175 -> 150; its getter is too tiny to hook), Black Belt
       counter (Stamina+32)/129 (`ActUtility.GetBlackBeltCounterAct` prefix rolls;
       BattlePassiveConstants.BlackBeltCounterPercentage set to 100 so the game's gate
       passes), Dragon Horn 25% one extra bounce (`SerialBattleUtility
       .CanContiniusJumpAttack` postfix, a new jump = no check for 4 s), MP crit cost
       Level/2 halved by equip ability 291 (`IsUseMpCriticalOccurrence` useMp), +25% magic
       relics don't stack and esper +25% physical/magic counts
       (`GetAccessory{Physical,Magic}CorrectionValue`; value logged first time to confirm
       multiplier vs fraction), undead slayers x2 vs undead (data: effective_species 1).
       Still todo: Offering 2 hits at -25% (PR "disorderly shoot", 4 at half:
       `CreateContinuousActData`), counters on misses too, True Knight cover rules, dual
       wield pairing rules (spears block the off-hand), undead slayer's 50% kill, Moogle
       Charm (faster Jump landing, Dance without the Dance status), Zephyr Cape SOS Haste,
       Ghost Ring Overcast, physical +25% also on heals, Hyper Wrist/Sneak Ring built-in
       boosts (not found in the API), Umaro relic odds, GP Rain formula, Mug only on the
       first dual-wield strike.
       Todo = "EL gained" message,
       weapon spell procs, unported relic bits, Spirit stat, evasion
       rework, Dance "Choreography", Rage odds, Blitz/Bushido changes, Tools,
       battle UI (enemy HP/status on X/Y, Scan hints), bug fixes,
       **Wait Gauge cast bars (user asked: "in battle i should see the cast bars like
       bnw") — v0.11.0, `WaitGauge.cs`, not yet seen in game**: the PR already waits
       between a command and its action ("chanting": per-ability `ability_wait`, Attack
       3, magic 5, summons 3, Jump air time 30, roughly SNES $10/$20/$E0;
       `BattleProgressATBFF6.GetChantingTime(actData)`). The plugin scales each chant time
       by BNW/vanilla CommandWaitTimes for the act's command (bnw_data.json command_waits;
       summons = ability type 9; Morph, vanilla 0, gets Fight's time per unit x $40), and
       `BattlePlayerInfomationController.UpdateATB(player, value)` shows the wait left
       (from `BattleProgressATB.chantingInfo` {actData, time}; the clock's direction and
       the full-bar value are learned at runtime and logged), tinting the bar
       (`view.atbGauge.gaugeImage`, `maxGaugeImage`) by status (ConditionType Stop, Slow,
       Heist = Haste). Original notes: BNW's config option "Wait Gauge" (on by default, c3.asm GaugeOn) makes
       a character's ATB bar show the chosen command's wait time draining until it
       runs (c1.asm WaitRemaining / DrawGauge "Rewritten to support Wait Gauge";
       ATB endcaps change once the bar is full). Needs BNW's per-command wait times
       too (c2.asm CommandWaitTimes at $C2067B: Fight/Item/Throw/Sketch/Rage/Dance
       $10, Magic/Bushido/Tools/Blitz/Lore/Control/Slot $20, Morph $40, others 0).
       **ATB bar color by status (user asked)**: ee.asm StatusATB picks the gauge
       palette: Stop red (213,41,41), Slow yellow (255,222,0), Haste green
       (41,213,41), otherwise white/grey (bin/palettes-bnw.bin palettes 7/3/6/5,
       brightest color shown); Stop wins over Slow over Haste.
8. [~] In-game testing on PC (user) and Steam Deck (deploy.sh; loads under Proton 11:
       BepInEx, Magicite, plugin, data check and guard self-test all OK on the Deck)
9. [ ] Package `pr/mods/BraveNewWorld/` for Magicite + plugin DLL
