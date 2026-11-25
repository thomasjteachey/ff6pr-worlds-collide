using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PrKit;
using Last.Battle;
using Last.Data.Master;
using Last.Data.User;
using Last.Interpreter;
using Last.Management;
using Last.Map;
using UnityEngine;
using Object = UnityEngine.Object;
using PrKit.Ff6;   // SnesAi: the kit's FF6 AI interpreter
using PrKit.Seeds;

namespace WorldsCollide.PR
{
    // Headless self-test (tools/wc_test_headless.ps1; no window, no sound): BepInEx/wc_test.request
    //   seed=..., preset=... or flags=..., seed2=... (a second seed: switching undoes the first),
    //   load=1 (then load the latest save through the hooked GotoLoadSaveData)
    //   inspect=21 (load that real save slot and report its completed / unfinished WC checks;
    //   read-only, with no seed generation or save)
    //   delete=1 (with seed2: the seed list's Delete refuses the current seed and one a save
    //   uses, then deletes the first seed through the screen's rows)
    //   msgs=1 (enemies' battle lines run the game's own Msg instead of the test answering them)
    //   newgame=1 (after the seed: New Game from the title; WC's start must put the seed's party
    //   on the Blackjack's deck, airship outside Narshe; WcEvents)
    //   worlds=1 (with newgame: the Blackjack's wheel -> World of Ruin, the Falcon where the
    //   Blackjack was); worlds=2 (the Falcon's wheel -> World of Balance, from its deck)
    //   tzen=1 (with newgame: warp to Tzen, buy from the esper seller: the seed's price and
    //   reward, the check's flag; talking again gives nothing)
    //   check=<name> (with newgame: a check's own test plan, CheckPlans: warp, run its script,
    //   gate closed / open, reward and flags). Airship ones: check=search_skies (the Falcon's
    //   (Search the skies.): gate, hidden once Doom Gaze's check is done, the fight from the
    //   wheel and what follows it), check=airship_crew (the unequip NPC's "Change party
    //   members."), check=eu_unequip (with -eu its unequip services take Gogo's and Umaro's
    //   equipment too; without, Gogo's only), check=eu_equip (-eu's equip menu, and the NoARBattle
    //   lambdas that share the PR's lock's code), check=reward_wallet (a won battle's -gpm gil in the
    //   wallet and on the screen, -xpm EXP on the characters), check=reward_mp (-mpm: a won battle's
    //   magic points on the equipped esper's spells and the result screen, the morph gauge unmultiplied)
    // makes the seeds through the real generator on the title screen, applies them live, checks
    // live rows and Magicite files against the seed folders, puts the game's own data back,
    // deletes the test seeds and quits. Lines start with "[wc test]"; "[wc test] done" ends it.
    static partial class WcTest
    {
        static Dictionary<string, string> req;
        enum Step { Off, Startup, Wait, Gen1, Gen2, Vanilla, Load, Loading, OperaCheckpoint, Battle, InBattle, NewGame, InNewGame, ToWor, ToFalconDeck, ToWob, Tzen, TzenBuy, TzenAgain, Plan, Done }
        static Step step = Step.Off;
        internal static bool Running => step != Step.Off;   // until the game quits
        internal static bool NoAssetChange => req != null && req.GetValueOrDefault("noasset", "0") == "1";
        internal static bool AllowsSave(int slot) => req != null && req.GetValueOrDefault("checkpoint", "") == "opera" &&
            int.TryParse(req.GetValueOrDefault("slot", "20"), out int wanted) && slot == wanted;
        static float since = -1, stepAt;
        static readonly List<string> made = new List<string>();
        static int failures;
        static string prevActive;
        static string lastReport = "";
        static float deckAt = -1;
        static List<(float, Func<bool>)> plan;
        static SaveSlotData loadedSlotData;
        static bool operaSaveStarted;
        static int operaCheckpointPhase;

        // tools/wc_events.py CHECK_FLAGS
        static readonly Dictionary<string, int> CheckFlags = new Dictionary<string, int>
        {
            ["tzen"] = 1, ["ancient_castle"] = 2, ["gau_father_house"] = 3, ["kohlingen"] = 4, ["tritoch"] = 5,
            ["zozo"] = 6, ["owzer_mansion"] = 7, ["zone_eater"] = 8, ["mt_zozo"] = 9, ["umaro_cave"] = 10,
            ["esper_mountain"] = 11, ["figaro_castle_wor"] = 12, ["collapsing_house"] = 13, ["narshe_wor"] = 14,
            ["magitek_factory"] = 15, ["doma_wor"] = 16, ["doma_wor_throne"] = 17,
            ["mobliz_wor"] = 18, ["doom_gaze"] = 19, ["magitek_factory_3"] = 20,
            ["lone_wolf"] = 21, ["lone_wolf_2"] = 22, ["daryl_tomb"] = 23, ["baren_falls"] = 24, ["south_figaro"] = 25,
            ["figaro_castle_wob"] = 26, ["mt_kolts"] = 27, ["narshe_wor_2"] = 28, ["magitek_factory_2"] = 29, ["doma_wor_3"] = 30,
            ["veldt"] = 31, ["fanatics_tower"] = 32, ["fanatics_tower_2"] = 33, ["ebots_rock"] = 34, ["burning_house"] = 35, ["whelk"] = 36,
            ["narshe_dragon"] = 37, ["mt_zozo_dragon"] = 38, ["opera_house_dragon"] = 39, ["kefka_tower_dragon_g"] = 40,
            ["kefka_tower_dragon_s"] = 41, ["ancient_castle_dragon"] = 42, ["phoenix_cave_dragon"] = 43, ["fanatics_tower_dragon"] = 44,
            ["auction_1"] = 45, ["auction_2"] = 46, ["kefka_tower"] = 47,
            ["floating_continent"] = 48, ["floating_continent_2"] = 49, ["floating_continent_3"] = 50, ["veldt_cave_wor"] = 51,
            ["lete_river"] = 52, ["sealed_gate"] = 53, ["serpent_trench"] = 54,
            ["phantom_train"] = 55, ["south_figaro_cave_wob"] = 56, ["doma_wob"] = 57, ["phoenix_cave"] = 58,
            ["narshe_battle"] = 59, ["opera_house_wob"] = 60, ["imperial_camp"] = 61, ["narshe_moogle_defense"] = 62,
        };

        // test names for a WC event's later rewards: (event module, reward index)
        // the 8 dragons: their PR "defeated" flags (ScenarioFlag2)
        static readonly Dictionary<string, int> Dragons = new Dictionary<string, int>
        {
            ["narshe_dragon"] = 138, ["mt_zozo_dragon"] = 108, ["opera_house_dragon"] = 178, ["kefka_tower_dragon_g"] = 225,
            ["kefka_tower_dragon_s"] = 220, ["ancient_castle_dragon"] = 175, ["phoenix_cave_dragon"] = 148, ["fanatics_tower_dragon"] = 166,
        };

        // checks that only mark themselves done (their item is in a chest: the chest port)
        static readonly HashSet<string> FlagOnly = new HashSet<string> { "fanatics_tower_2" };

        static readonly Dictionary<string, (string, int)> SubChecks = new Dictionary<string, (string, int)>
        {
            ["doma_wor_throne"] = ("doma_wor", 1), ["magitek_factory_3"] = ("magitek_factory", 2), ["lone_wolf_2"] = ("lone_wolf", 1),
            ["narshe_wor_2"] = ("narshe_wor", 1), ["fanatics_tower_2"] = ("fanatics_tower", 1),
            ["narshe_dragon"] = ("eight_dragons", 0), ["mt_zozo_dragon"] = ("eight_dragons", 1), ["opera_house_dragon"] = ("eight_dragons", 2),
            ["kefka_tower_dragon_g"] = ("eight_dragons", 3), ["kefka_tower_dragon_s"] = ("eight_dragons", 4),
            ["ancient_castle_dragon"] = ("eight_dragons", 5), ["phoenix_cave_dragon"] = ("eight_dragons", 6), ["fanatics_tower_dragon"] = ("eight_dragons", 7),
            ["auction_1"] = ("auction_house", 0), ["auction_2"] = ("auction_house", 1), ["final_kefka"] = ("kefka_tower", 0),
            ["floating_continent_2"] = ("floating_continent", 1), ["floating_continent_3"] = ("floating_continent", 2), ["magitek_factory_2"] = ("magitek_factory", 1), ["doma_wor_3"] = ("doma_wor", 2),
            ["search_skies"] = ("doom_gaze", 0),   // the Falcon wheel's way to Doom Gaze's fight (its check flag: doom_gaze's)
        };

        // checks whose test is: warp to (map, point 1), gate, run (script), reward
        static readonly Dictionary<string, (int, string)> SimpleChecks = new Dictionary<string, (int, string)>
        {
            ["ancient_castle"] = (519, "sc_e_0890"), ["gau_father_house"] = (73, "sc_e_0097"), ["kohlingen"] = (98, "sc_e_0285"),
            ["zozo"] = (395, "sc_e_0303"), ["owzer_mansion"] = (446, "sc_e_0775"), ["zone_eater"] = (507, "sc_e_0865"),
            ["mt_zozo"] = (474, "sc_e_0735"), ["umaro_cave"] = (496, "sc_e_0790"),
            // post-boss checks: the scene after the fight, without the fight
            ["esper_mountain"] = (425, "sc_e_0547_1"), ["figaro_castle_wor"] = (440, "sc_e_0672_1"),
            ["collapsing_house"] = (201, "sc_e_0639"), ["narshe_wor"] = (10, "sc_e_0873"),
            ["magitek_factory"] = (398, "sc_e_0400"), ["doma_wor"] = (300, "sc_e_0939_2"), ["doma_wor_throne"] = (304, "sc_e_0940"),
            ["mobliz_wor"] = (207, "sc_e_0709_3"), ["doom_gaze"] = (268, "sc_e_0964_2"),
            ["magitek_factory_3"] = (143, "sc_e_0404_8"), // after the mine cart, at WC's former Setzer meetup
            ["lone_wolf"] = (20, "sc_e_0438"), ["lone_wolf_2"] = (20, "sc_e_0439"), ["daryl_tomb"] = (577, "sc_e_0703"),
            ["baren_falls"] = (376, "sc_e_0218_2"), ["south_figaro"] = (50, "sc_e_0079"), ["figaro_castle_wob"] = (34, "sc_e_0029"),
            ["mt_kolts"] = (343, "sc_e_0055_1"), ["narshe_wor_2"] = (10, "sc_e_0873"), ["magitek_factory_2"] = (402, "sc_e_0401_1"),
            ["doma_wor_3"] = (521, "sc_e_0901_1"), ["veldt"] = (574, "sc_e_0239_b"),
            ["fanatics_tower"] = (476, "sc_e_0776"), ["fanatics_tower_2"] = (486, "sc_e_0881_1"),
            ["ebots_rock"] = (540, "sc_e_0952_1"), ["burning_house"] = (420, "sc_e_0539_1"), ["whelk"] = (323, "sc_e_0011_1"),
            ["kefka_tower"] = (554, "sc_e_0977_1"),
            ["floating_continent"] = (431, "sc_e_0566"), ["floating_continent_2"] = (431, "sc_e_0592_1"),
            ["floating_continent_3"] = (119, "sc_e_0607_1"), ["veldt_cave_wor"] = (491, "sc_e_0744_2"),
            ["lete_river"] = (347, "sc_e_0065_1"), ["serpent_trench"] = (96, "sc_e_1502_3"),
            ["phantom_train"] = (356, "sc_e_0214_1"), ["south_figaro_cave_wob"] = (329, "sc_e_0086_1"),
            ["doma_wob"] = (75, "sc_e_0110_1"), ["phoenix_cave"] = (499, "sc_e_0828"),
            ["opera_house_wob"] = (118, "sc_e_0344_2"), ["imperial_camp"] = (350, "sc_e_0128_1"), ["narshe_moogle_defense"] = (318, "sc_e_0018_2"), ["narshe_battle"] = (19, "sc_e_0259_1"),
        };

        // a check's test: (seconds to wait before, action; false = stop). Ends on the field.
        // WC's objective results (WcObjectives): a seed whose objectives are all met from the start
        // (flags in the request); a few seconds on the deck, then each result
        static List<(float, Func<bool>)> ObjectivePlan()
        {
            var seed = WcSeeds.Active;
            Last.Data.User.OwnedCharacterData Char(int id)
            {
                foreach (var c in UserDataManager.Instance().OwnedCharacterList) if (c?.characterStatusId == id) return c;
                return null;
            }
            bool Knows(Last.Data.User.OwnedCharacterData c, int id)
            {
                foreach (var a in c.OwnedAbilityList) if (a?.Ability?.Id == id && a.SkillLevel >= 100) return true;
                return false;
            }
            Func<bool> results = () =>
            {
                var party = UserDataManager.Instance().GetPartyMemberData();
                for (int i = 0; i < seed.Objectives.Count; i++)
                {
                    var o = seed.Objectives[i];
                    if (!new[] { "Vigor All", "Stamina Random", "Learn SwdTechs", "Dried Meat", "Full Heal", "Learn Spells", "Fallen One", "Imp Song", "Sour Mouth", "Forget Spells",
                                 "Learn Rages", "Magitek Upgrade", "Max Morph Duration" }.Contains(o.ResultName)) continue;
                    if (o.Conditions.Any(k => k.Type != "Characters" || k.Count > 3)) continue;   // the preset's own, not met at the start
                    Check(WcObjectives.IsComplete(i), $"objective {o.Letter} ({o.Result}) complete");
                    switch (o.ResultName)
                    {
                        case "Vigor All":
                            var vig = new List<string>();
                            foreach (var c in party) vig.Add($"{c.characterStatusId}:{c.Parameter.AddtionalPower}");
                            Check(party.ToArray().All(c => c.Parameter.AddtionalPower >= o.IntArgs[0]), $"{o.Result}: party vigor bonus [{string.Join(" ", vig)}]");
                            break;
                        case "Stamina Random":
                            var target = Char(o.IntArgs[2] + 1);
                            Check(target == null || target.Parameter.AddtionalVitality >= o.IntArgs[0],
                                  $"{o.Result}: character {o.IntArgs[2] + 1} {(target == null ? "not owned yet" : $"stamina bonus {target.Parameter.AddtionalVitality}")}");
                            break;
                        case "Learn SwdTechs":
                            // the techniques go to a Bushido holder (WC randomizes commands), else Cyan
                            var holder = WcSkills.Holder(12);
                            if (holder == null && Char(3) == null) WcEvents.Join(3);   // Cyan joins: the deferred result
                            var learner = WcSkills.Holder(12) ?? Char(3);
                            var known = Enumerable.Range(124, 8).Count(id => learner != null && Knows(learner, id));
                            Check(known >= Math.Min(8, o.IntArgs[0]), $"{o.Result}: character {learner?.characterStatusId} knows {known} of 8 Bushido; {WcSkills.LastSync}");
                            break;
                        case "Forget Spells":
                            Check(seed.ForgetTable.Count > 0, $"{o.Result}: the seed's forget order has {seed.ForgetTable.Count} spells");
                            break;
                        case "Learn Rages":
                            // to a Rage holder (WC randomizes commands), else Gau, who may join for it
                            if (WcSkills.Holder(23) == null && Char(12) == null) WcEvents.Join(12);
                            var rager = WcSkills.Holder(23) ?? Char(12);
                            int rages = rager == null ? 0 : seed.RageTable.Count(id => WcSkills.Knows(rager, id));
                            Check(seed.RageTable.Count == 255 && rages >= o.IntArgs[0],
                                  $"{o.Result}: character {rager?.characterStatusId} knows {rages} rages of the seed's order ({seed.RageTable.Count})");
                            break;
                        case "Magitek Upgrade":
                            {
                                // the armor's basic moves (as riding it gives them), then Terra's four for everyone
                                var rider = party[0];
                                WcSkills.Teach(rider, 218);
                                WcObjectives.MagitekUpgrade();
                                Check(new[] { 221, 223, 224, 225 }.All(id => WcSkills.Knows(rider, id)), $"{o.Result}: character {rider.characterStatusId} has Bio Blast/Confuser/X-Fer/Tek Missile");
                                break;
                            }
                        case "Max Morph Duration":
                            Check(party.ToArray().All(c => c.MagicLearningValue == 255),
                                  $"{o.Result}: morph supply [{string.Join(" ", party.ToArray().Select(c => $"{c.characterStatusId}:{c.MagicLearningValue}"))}]");
                            break;
                        case "Learn Spells":
                            var lead = party[0];
                            int knownSpells = seed.SpellTable.Count(id => Knows(lead, id));
                            int forgot = seed.Objectives.Where(x => x.ResultName == "Forget Spells").Sum(x => x.IntArgs.Length > 0 ? x.IntArgs[0] : 0);
                            Check(knownSpells >= o.IntArgs[0] - forgot, $"{o.Result}: character {lead.characterStatusId} knows {knownSpells} of the seed's spell table (forget results: {forgot})");
                            break;
                        case "Fallen One":
                            var hp = new List<string>();
                            foreach (var c in party) hp.Add($"{c.characterStatusId}:{c.Parameter.CurrentHP}");
                            Check(party.ToArray().All(c => c.Parameter.CurrentHP == 1), $"{o.Result}: party HP [{string.Join(" ", hp)}]");
                            break;
                        case "Imp Song":
                        case "Sour Mouth":
                            var st = new List<string>();
                            foreach (var c in party)
                            {
                                var ids = new List<int>();
                                foreach (var x in c.Parameter.CurrentConditionList) if (x != null) ids.Add(x.Id);
                                st.Add($"{c.characterStatusId}:[{string.Join(",", ids)}]");
                            }
                            Check(party.ToArray().All(c => { foreach (var x in c.Parameter.CurrentConditionList) if (x?.Id == 13) return true; return false; }),
                                  $"{o.Result}: party statuses {string.Join(" ", st)}");
                            break;
                        case "Dried Meat":
                            var meat = UserDataManager.Instance().SearchOwnedItem(o.Items[0][0])?.Count ?? 0;
                            Check(meat >= 1, $"{o.Result}: {meat} owned (content {o.Items[0][0]})");
                            break;
                    }
                }
                // "Add Enemy Levels" / "Auto ...": a battle with the bonus (WcScaling's report)
                int bonus = WcObjectives.LevelBonus("Add Enemy Levels");
                if (bonus > 0 || seed.Objectives.Any(o => o.ResultName.StartsWith("Auto ")))
                {
                    Plugin.L.LogInfo($"[wc test] enemy level bonus +{bonus}: a test battle");
                    stepAt = Time.realtimeSinceStartup;
                    step = Step.Battle;
                }
                return true;
            };
            return new List<(float, Func<bool>)> { (5f, results) };
        }

        // WC's Track menu: the field menu's entry, its pick opening the Track pages, the pages
        // WC's boss shuffles / -fer (WcBattles): an EncountBoss command, as an event script runs it,
        // for a party the seed swaps: the battle that starts is the seed's party, and the command's
        // operands are back to the script's after it
        static List<(float, Func<bool>)> BattlePlan()
        {
            var s = WcSeeds.Active;
            int from = 0, want = 0;
            string quest = null;
            Func<bool> start = () =>
                {
                    Check(s != null && s.Battles.Count > 0, $"battles: the seed swaps {s?.Battles.Count ?? 0} event parties");
                    if (s == null || s.Battles.Count == 0) return false;
                    var boss = s.Battles.Where(kv => kv.Value.Length == 1 && s.Bosses.Any(b => b.Party == kv.Key)).Select(kv => kv.Key).DefaultIfEmpty(s.Battles.Keys.First()).First();
                    // a battle quest's party WC swapped, if the seed has one (its quest must still count)
                    quest = s.Quests.Where(q => q.Value.Any(p => s.Battles.ContainsKey(p))).Select(q => q.Key).FirstOrDefault();
                    if (quest != null) boss = s.Quests[quest].First(p => s.Battles.ContainsKey(p));
                    from = boss;
                    want = WcBattles.Map(from);
                    Check(WcBattles.Map(from) == want && want != from, $"battles: party {from} -> {want} (the same each time)");
                    var mc = new MainCore();
                    var op = new global::Last.Interpreter.Instructions.Format.Operands();
                    op.iValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(8);
                    op.rValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<double>(8);
                    op.sValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(8);
                    for (int k = 0; k < 8; k++) op.sValues[k] = "";
                    op.iValues[0] = from;
                    var mn = new global::Last.Interpreter.Instructions.Format.Mnemonic();
                    mn.mnemonic = "EncountBoss";
                    mn.operands = op;
                    mc.currentInstruction = mn;
                    if (mc.op == null || mc.op.iValues[0] != from) { Check(false, "battles: a test MainCore's operands"); return false; }
                    Plugin.L.LogInfo($"[wc test] EncountBoss party {from}");
                    var r = global::Last.Interpreter.Instructions.External.Misc.EncountBoss(mc);
                    Check(op.iValues[0] == from, $"battles: EncountBoss (returned {r}) left the script's party {op.iValues[0]} in place; swap: {WcBattles.LastSwap}");
                    return true;
                };
            return new List<(float, Func<bool>)>
            {
                (0f, start),
                (12f, () =>
                {
                    var b = BattlePlugManager.Instance();
                    int got = b?.MonsterParty?.Id ?? -1;
                    var ids = b?.GetEnemyUnits()?.Select(e => e?.GetMonster()?.Id ?? 0).Where(i => i > 0).ToList() ?? new List<int>();
                    var party = MasterManager.Instance.GetData<MonsterParty>(want);
                    var expect = party == null ? new List<int>() : new[] { party.Monster1, party.Monster2, party.Monster3, party.Monster4, party.Monster5, party.Monster6, party.Monster7, party.Monster8, party.Monster9 }.Where(x => x > 0).ToList();
                    Check(b != null && b.IsBattle() && (got == want || (ids.Count > 0 && ids.All(expect.Contains))),
                          $"battles: the battle is party {got}, monsters [{string.Join(" ", ids)}] (the seed's {want}: monsters [{string.Join(" ", expect)}])");
                    Check(WcBattles.CurrentOriginal == from, $"battles: the battle came from the script's party {WcBattles.CurrentOriginal} ({from})");
                    if (quest != null)
                    {
                        WcTrack.BattleWon(-1);   // as if won (Exp_Postfix)
                        Check(WcTrack.QuestDone(quest), $"battles: quest '{quest}' done after its (swapped) battle");
                    }
                    WcTrack.SetQuest("Win An Auction");
                    Check(WcTrack.QuestDone("Win An Auction"), "quests: Win An Auction's flag");
                    Check(Last.Defaine.ConditionType.Reraise == (Last.Defaine.ConditionType)602 && Last.Defaine.ConditionType.Slip == (Last.Defaine.ConditionType)407,
                          $"auto statuses: Reraise {(int)Last.Defaine.ConditionType.Reraise}, Slip {(int)Last.Defaine.ConditionType.Slip}, DoomZonbie {(int)Last.Defaine.ConditionType.DoomZonbie} (condition table: 602, 407)");
                    return false;
                }),
            };
        }

        // The Cultists' Tower's magic-only status goes with the tower, not the boss (wc_seed.place_script,
        // WcBattles.TowerStart; the user's seed gaadl68ix7j4, 2026-11-12: MagiMaster at
        // Tritoch's cliff, "i was unable to use attack"). Each swapped event battle into or out of the
        // tower is started as its event does (EncountBoss with the script's party), the party's
        // TowerOfZealots condition read in battle, the battle ended by force. Order: away from the
        // tower first, the tower's, then away again (the status mustn't carry over)
        static List<(float, Func<bool>)> TowerPlan()
        {
            var s = WcSeeds.Active;
            const int Tower = 9024;
            var mm = MasterManager.Instance;
            int ScriptOf(int p) => mm.GetData<MonsterParty>(p)?.ScriptNameId ?? -1;
            int Origin(int p) => s != null && s.LookSources.TryGetValue(p, out var o) ? o : p;
            var into = new List<(int From, int To)>();
            var away = new List<(int From, int To)>();
            foreach (var from in s?.Battles.Keys.OrderBy(k => k) ?? Enumerable.Empty<int>())
            {
                int to = WcBattles.Map(from);
                if (to == from) continue;
                if (ScriptOf(from) == Tower) into.Add((from, to));
                else if (ScriptOf(Origin(to)) == Tower) away.Add((from, to));
            }
            var cases = new List<(int From, int To, bool Want)>();
            if (away.Count > 0) cases.Add((away[0].From, away[0].To, false));
            foreach (var c in into) cases.Add((c.From, c.To, true));
            foreach (var c in away.Skip(1)) cases.Add((c.From, c.To, false));
            var steps = new List<(float, Func<bool>)>
            {
                (0f, () =>
                {
                    Check(cases.Count > 0, $"tower: {into.Count} tower events swapped, {away.Count} events fighting a tower party elsewhere: " +
                          string.Join(", ", cases.Select(c => $"{c.From} -> {c.To} (script {ScriptOf(c.To)}, {(c.Want ? "tower" : "away")})")));
                    recorder = () =>
                    {
                        var b = BattlePlugManager.Instance();
                        if (b == null || !b.IsBattle()) return;
                        foreach (var u in b.GetPlayerUnits())
                        {
                            var pr = u?.BattleUnitDataInfo?.Parameter;
                            if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999)
                            {
                                if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999;
                                pr.CurrentHP = 9999;
                            }
                        }
                    };
                    return cases.Count > 0;
                }),
            };
            foreach (var (from, to, want) in cases)
            {
                int startsBefore = 0;
                bool plugin = want && ScriptOf(to) != Tower;
                bool ended = false;
                steps.Add((2f, () =>
                {
                    startsBefore = WcBattles.TowerStarts;
                    var mc = new MainCore();
                    var op = new global::Last.Interpreter.Instructions.Format.Operands();
                    op.iValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(8);
                    op.rValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<double>(8);
                    op.sValues = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(8);
                    for (int k = 0; k < 8; k++) op.sValues[k] = "";
                    op.iValues[0] = from;
                    var mn = new global::Last.Interpreter.Instructions.Format.Mnemonic();
                    mn.mnemonic = "EncountBoss";
                    mn.operands = op;
                    mc.currentInstruction = mn;
                    Plugin.L.LogInfo($"[wc test] tower: EncountBoss party {from} (the seed's {to}, script {ScriptOf(to)})");
                    global::Last.Interpreter.Instructions.External.Misc.EncountBoss(mc);
                    return true;
                }));
                steps.Add((12f, (Func<bool>)(() =>
                {
                    var b = BattlePlugManager.Instance();
                    bool on = b != null && b.IsBattle();
                    var units = new List<BattleUnitData>();
                    if (on) foreach (var p in b.GetPlayerUnits()) if (p != null) units.Add(p);
                    int with = 0;
                    foreach (var u in units)
                    {
                        var list = u.BattleUnitDataInfo?.Parameter?.CurrentConditionList;
                        bool has = false;
                        if (list != null)
                            foreach (var c in list)
                                if (c != null && c.ConditionType == (int)Last.Defaine.ConditionType.TowerOfZealots) has = true;
                        if (has) with++;
                    }
                    var foes = on ? string.Join(" ", b.GetEnemyUnits().Select(e => e?.GetMonster()?.Id ?? 0).Where(i => i > 0)) : "";
                    Check(on && units.Count > 0 && with == (want ? units.Count : 0),
                          $"tower: party {from} -> {to} ({(want ? "at the tower" : "away from it")}, script {ScriptOf(to)}, monsters [{foes}]): " +
                          $"{with} of {units.Count} party members have the tower's magic-only status (want {(want ? "all" : "none")})");
                    int starts = WcBattles.TowerStarts - startsBefore;
                    Check(starts == (plugin ? 1 : 0), $"tower: the plugin gave the status {starts} time(s) (want {(plugin ? 1 : 0)}: " +
                          $"{(plugin ? "the boss's own script" : want ? "the tower's script does it" : "none away from the tower")})");
                    var j = b?.BattleEndJugment;
                    if (j != null)
                    {
                        j.SetEnabled(true);
                        j.CurrentEndJugmentType = BattleEndJugment.EndJugmentType.Normal;
                        j.isForce = true;
                    }
                    return on;
                })));
                for (int w = 0; w < 10; w++)
                    steps.Add((3f, () =>
                    {
                        if (ended) return true;
                        var b = BattlePlugManager.Instance();
                        if (b != null && b.IsBattle()) return true;
                        ended = Object.FindObjectOfType<FieldMap>() != null;
                        if (ended) Plugin.L.LogInfo($"[wc test] tower: battle {to} over, back in the field");
                        return true;
                    }));
                steps.Add((1f, () =>
                {
                    Check(ended, $"tower: back in the field after battle {to}");
                    if (!ended) recorder = null;
                    return ended;
                }));
            }
            steps.Add((0.5f, () => { recorder = null; return false; }));
            return steps;
        }

        // --- walking, the way a player does: the field's key controller gets the stick/d-pad axis
        // every frame (FieldPlayerKeyController.OnTouchPadCallback: collisions and the map's step
        // triggers as in play), until the map changes or the time is up
        static Vector2 walkAxis;
        static float walkUntil = -1;
        static int walkFrom;

        static Last.Map.FieldController Field() => Object.FindObjectOfType<FieldMap>()?.fieldController;

        static void Walk(float x, float y, float seconds)
        {
            walkAxis = new Vector2(x, y);
            walkUntil = Time.realtimeSinceStartup + seconds;
            walkFrom = UserDataManager.Instance()?.CurrentMapId ?? -1;
            Plugin.L.LogInfo($"[wc test] walking ({x},{y}) for up to {seconds} s from map {walkFrom} at {WcEvents.PlayerCell()}");
        }

        static void WalkTick()
        {
            if (walkUntil < 0) return;
            try
            {
                bool stop = Time.realtimeSinceStartup > walkUntil || (UserDataManager.Instance()?.CurrentMapId ?? walkFrom) != walkFrom;
                var list = Field()?.FieldPlayerControllerList;
                if (list != null)
                    foreach (var c in list)
                    {
                        var k = c?.TryCast<Last.Map.FieldPlayerKeyController>();
                        if (k != null) k.OnTouchPadCallback(stop ? Vector2.zero : walkAxis);
                    }
                if (stop)
                {
                    walkUntil = -1;
                    Plugin.L.LogInfo($"[wc test] walk ended on map {UserDataManager.Instance()?.CurrentMapId} at {WcEvents.PlayerCell()}");
                }
            }
            catch (Exception) { /* the field mid-change: the next frame has the new one */ }
        }

        // a script the test started (EventRunScript without callbacks) leaves the interpreter
        // marked busy once it ends, and the game then refuses its own scripts too (an arrival
        // script after ChangeMap, a map's exit event): marked idle as soon as no script runs
        static void IdleInterpreter()
        {
            var integ = Integrator.Instance;
            if (integ == null || !integ.working || integ.IsMainCoreAlive() || integ.ExistsReserveScript()) return;
            integ.working = false;
            WcEvents.Running = false;
        }

        static Last.Map.TransportationInfo Riding()
        {
            try { return Field()?.Last_Map_IEntityAccessor_GetCurrentTransportation(); } catch (Exception) { return null; }
        }

        static string Ship(int id)
        {
            try
            {
                var t = Field()?.Last_Map_IEntityAccessor_GetTransportation(id);
                if (t == null) return "none";
                var o = t.MapObject;
                return $"enable {t.Enable}, stays on map {t.StayMapId}, on the field {(o != null && o.gameObject.activeInHierarchy ? "yes" : "no")}";
            }
            catch (Exception e) { return "? " + e.Message; }
        }

        static string FieldEntity(int id)
        {
            try { return Field()?.GetFieldEntity(id) != null ? "+" : "-"; }
            catch (Exception) { return "?"; }
        }

        // the user's playtest, 2026-11-06, as a test: a New Game, the wheel, landing, walking into
        // Narshe (the WoB's Narshe: its WoB exits, no "after the collapse"), walking out (the WoB map,
        // the Blackjack still there). narshe=wor: the wheel's (Fly to the World of Ruin.): on the
        // WoR map riding the Falcon, then the WoR's Narshe and back to the WoR map with the Falcon
        static List<(float, Func<bool>)> NarshePlan(bool wor)
        {
            int F2(int i) => WcEvents.Flag(DataStorage.Category.kScenarioFlag2, i);
            int world = wor ? WcEvents.WorMap : WcEvents.WobMap, ship = wor ? WcEvents.FalconId : WcEvents.BlackjackId;
            string shipName = wor ? "Falcon" : "Blackjack";
            const int Narshe = 3;
            // Narshe's exits (map 20010's ev_e_0609 / ev_e_0609_1): the WoB's lead to the WoB mines
            // (maps 321, 322, 317), the WoR's to the WoR's (639, 638, 637)
            int[] wobExits = { 17, 21, 19, 94 }, worExits = { 198, 199, 200, 201 };
            string Exits(int[] ids) => string.Join(" ", ids.Select(FieldEntity));

            Func<bool> wheel = () =>
            {
                if (wor)
                {
                    // a world switch keeps the airship's cell (WC's ToggleWorlds): park the Blackjack at
                    // WoR Narshe's cell first (115,34; in the WoB it's open country), so the Falcon
                    // arrives where it can land, outside WoR Narshe
                    var ts = UserDataManager.Instance()?.OwnedTransportationList;
                    if (ts != null)
                        foreach (var t in ts)
                            if (t.Id == WcEvents.BlackjackId) t.Position = new Vector3(115, 34, t.Position.z);
                }
                WcEvents.TestAnswerKeys.Enqueue(wor ? "WC_HELM_WOR" : "E0412_00_119_c_01");
                Check(WcEvents.RunScript("sc_e_BJoutboard"), wor ? "the wheel: (Fly to the World of Ruin.)" : "the wheel: (Take the wheel.)");
                return true;
            };
            Func<bool> flying = () =>
            {
                var cur = Riding();
                int map = UserDataManager.Instance()?.CurrentMapId ?? -1;
                Check(map == world && cur != null && cur.id == ship,
                      $"in the air on map {map} riding {(cur == null ? "nothing" : cur.id.ToString())} (want map {world}, the {shipName} {ship}); {shipName}: {Ship(ship)}");
                try { Field().TransportationPlaneLanding(); Plugin.L.LogInfo("[wc test] landing"); }
                catch (Exception e) { Check(false, "landing: " + e.Message); }
                return true;
            };
            Func<bool> landed = () =>
            {
                var cur = Riding();
                bool onFoot = cur == null || cur.id != ship;
                Check(onFoot, $"landed: on foot at {WcEvents.PlayerCell()} (riding {(cur == null ? "nothing" : cur.id.ToString())})");
                Check(Ship(ship).Contains($"stays on map {world}") && Ship(ship).Contains("on the field yes"), $"the {shipName} stands on map {world}: {Ship(ship)}");
                Walk(0, 1, 5);   // north: Narshe's entrance is the cell north of where the airship parks
                return true;
            };
            Func<bool> inNarshe = () =>
            {
                int map = UserDataManager.Instance()?.CurrentMapId ?? -1;
                Check(map == Narshe, $"walked into Narshe: map {map} at {WcEvents.PlayerCell()}");
                int collapsed = F2(WcEvents.CollapseFlag);
                Check(collapsed == (wor ? 1 : 0), $"{(wor ? "the WoR's" : "the WoB's")} Narshe: 'after the collapse' (ScenarioFlag2 41) = {collapsed}, Falcon flag = {F2(WcEvents.FalconFlag)}");
                string wob = Exits(wobExits), wr = Exits(worExits);
                Check(wor ? (wr.Contains("+") && !wob.Contains("+")) : (wob.Contains("+") && !wr.Contains("+")),
                      $"Narshe's exits (to its mines): WoB set [{wob}], WoR set [{wr}] (want the {(wor ? "WoR" : "WoB")}'s)");
                Walk(0, -1, 8);   // south, out of town
                return true;
            };
            Func<bool> outside = () =>
            {
                int map = UserDataManager.Instance()?.CurrentMapId ?? -1;
                Check(map == world, $"walked out of Narshe onto map {map} (want {world}) at {WcEvents.PlayerCell()}");
                Check(Ship(ship).Contains($"stays on map {world}") && Ship(ship).Contains("on the field yes"), $"the {shipName} is on this map: {Ship(ship)}");
                Check(F2(WcEvents.CollapseFlag) == (wor ? 1 : 0), $"'after the collapse' = {F2(WcEvents.CollapseFlag)} on map {map}; {WcEvents.LastSync}");
                return false;
            };
            var steps = new List<(float, Func<bool>)> { (3f, wheel), (10f, flying), (8f, landed) };
            steps.Add((8f, inNarshe));
            steps.Add((10f, outside));
            return steps;
        }

        // the world sync (WcEvents.WorldSync): on foot on the WoR map with the WoB's flags and the
        // Falcon elsewhere (how the user's game left Narshe for the WoR, 2026-11-06), the flags turn
        // to the WoR's and the Falcon comes to the party; back on the WoB map, the WoB's again
        static List<(float, Func<bool>)> WorldSyncPlan()
        {
            int F2(int i) => WcEvents.Flag(DataStorage.Category.kScenarioFlag2, i);
            Func<bool> toWor = () =>
            {
                var ts = UserDataManager.Instance()?.OwnedTransportationList;
                if (ts != null) foreach (var t in ts) if (t.Id == WcEvents.FalconId) t.MapId = WcEvents.WobMap;
                DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, WcEvents.CollapseFlag, 0);
                DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, WcEvents.FalconFlag, 0);
                DataStorage.Instance.Set(DataStorage.Category.kTreasureFlag4, 136, 0); // visible/enabled but not boardable
                Check(WcEvents.GotoMap(WcEvents.WorMap, 20), "warp on foot to the WoR map (WoR Narshe's door)");
                return true;
            };
            Func<bool> onWor = () =>
            {
                var r = WcEvents.Report();
                Check(UserDataManager.Instance()?.CurrentMapId == WcEvents.WorMap && F2(WcEvents.CollapseFlag) == 1 && F2(WcEvents.FalconFlag) == 1 &&
                      r.Contains("Falcon map 2") && r.Contains("enable True") &&
                      WcEvents.Flag(DataStorage.Category.kTreasureFlag4, 136) == 1,
                      $"WoR map: collapse flag {F2(WcEvents.CollapseFlag)}, Falcon flag {F2(WcEvents.FalconFlag)}; {r}; {WcEvents.LastSync}; player cell {WcEvents.PlayerCell()}");
                DataStorage.Instance.Set(DataStorage.Category.kTreasureFlag4, 135, 0); // same repair for the Blackjack
                Check(WcEvents.GotoMap(WcEvents.WobMap, 1), "warp on foot to the WoB map (Narshe's door)");
                return true;
            };
            Func<bool> onWob = () =>
            {
                var r = WcEvents.Report();
                Check(UserDataManager.Instance()?.CurrentMapId == WcEvents.WobMap && F2(WcEvents.CollapseFlag) == 0 && F2(WcEvents.FalconFlag) == 0 &&
                      r.Contains("Blackjack map 1") && WcEvents.Flag(DataStorage.Category.kTreasureFlag4, 135) == 1,
                      $"WoB map: collapse flag {F2(WcEvents.CollapseFlag)}, Falcon flag {F2(WcEvents.FalconFlag)}; {r}; {WcEvents.LastSync}; player cell {WcEvents.PlayerCell()}");
                Check(WcAchievements.InWcGame(), $"achievements off in this WC game ({WcAchievements.Blocked} updates blocked so far)");
                return false;
            };
            return new List<(float, Func<bool>)> { (3f, toWor), (10f, onWor), (10f, onWob) };
        }

        // rooms a WC game reaches out of story order (the user's playtest, 2026-11-06): Narshe's
        // treasure room has its locked chest (and its owner), Narshe's weapon shop is the WoB one
        // (group 44, randomized; not the opening's 18), South Figaro isn't occupied and Celes is
        // chained in its basement for the check
        // An audit of WC's start state (its event modules' init_event_bits, PORTING.md "story
        // flags"): each town and dungeon visited in a fresh WC game, what plays on arrival logged
        // (where the party ended up, the scripts that ran, the story lines play would show or skip).
        // A vanilla scene playing on arrival marks a place WC's start leaves in another state.
        static readonly (string Name, int Map, int Point)[] TourWob =
        {
            ("Narshe", 3, 1), ("Figaro Castle", 21, 1), ("Duncan's Cabin", 36, 6), ("South Figaro", 38, 1),
            ("The Returners' Hideout", 66, 1), ("Cabin", 73, 1), ("Doma Castle", 75, 1), ("Mobliz", 84, 1),
            ("Nikeah", 92, 1), ("Kohlingen", 97, 1), ("Dragon's Neck Cabin", 103, 1), ("Jidoor", 105, 1),
            ("Opera House", 112, 1), ("Albrook", 125, 1), ("Tzen", 132, 1), ("Maranda", 138, 1), ("Vector", 143, 1),
            ("Imperial Observation Post", 162, 1), ("Thamasa", 180, 1), ("Chocobo Stable", 188, 1),
            ("Narshe Mines", 317, 1), ("South Figaro Cave", 328, 3), ("Mt. Kolts", 334, 1), ("Lethe River", 345, 1),
            ("Imperial Camp", 350, 11), ("Phantom Forest", 351, 1), ("Phantom Train", 356, 1), ("Baren Falls", 375, 2),
            ("Crescent Mountain Cave", 377, 2), ("Serpent Trench", 380, 1), ("Zozo", 383, 1), ("Magitek Factory", 396, 1),
            ("Cave to the Sealed Gate", 405, 1), ("Burning Home", 412, 1), ("Esper Cave", 421, 3), ("Owzer's Mansion", 442, 1),
        };
        static readonly (string Name, int Map, int Point)[] TourWor =
        {
            ("Solitary Island", 190, 1), ("Crumbling House", 433, 1), ("Darill's Tomb", 455, 1), ("Mt. Zozo", 469, 2),
            ("Cultists' Tower", 476, 1), ("Cave on the Veldt", 488, 1), ("Yeti's Cave", 493, 1), ("Phoenix Cave", 497, 1),
            ("Zone Eater's Belly", 500, 1), ("Cave to the Ancient Castle", 508, 1), ("Ancient Castle", 512, 1),
            ("Ebot's Rock", 539, 32), ("Kefka's Tower", 542, 1), ("Duncan's Cabin (WoR)", 272, 1), ("Kohlingen (WoR)", 262, 1),
        };

        static List<(float, Func<bool>)> TourPlan(bool wor)
        {
            var plan = new List<(float, Func<bool>)>();
            if (wor)
                plan.Add((0f, () =>
                {
                    // the World of Ruin as WC's wheel leaves it (ScenarioFlag2 41 after the collapse, 97 the Falcon)
                    DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 41, 1);
                    DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 97, 1);
                    return true;
                }));
            int exits = 0;
            foreach (var p in wor ? TourWor : TourWob)
            {
                var place = p;
                plan.Add((1f, () =>
                {
                    WcEvents.TestMessages.Clear();
                    WcEvents.TestStoryShown.Clear();
                    WcEvents.TestStorySkipped.Clear();
                    exits = WcEvents.MainExits;
                    Check(WcEvents.GotoMap(place.Map, place.Point), $"tour: to {place.Name} (map {place.Map})");
                    return true;
                }));
                plan.Add((6f, () =>
                {
                    int at = UserDataManager.Instance()?.CurrentMapId ?? -1;
                    var lines = WcEvents.TestMessages.Where(m => !m.StartsWith("WC_")).Select(m => m.Split(':')[0]).ToList();
                    // the map's live scripted entities (object type / action : script id): what a step
                    // or a press here would start
                    var scripted = new List<string>();
                    try
                    {
                        var list = Field()?.entityList;
                        if (list != null)
                            foreach (var e in list)
                            {
                                var pe = e?.Property?.TryCast<Last.Map.PropertyEvent>();
                                if (pe != null && pe.ScriptId > 0 && e.gameObject.activeInHierarchy)
                                    scripted.Add($"{pe.ObjectType}/{pe.ActionId}:{pe.ScriptId}");
                            }
                    }
                    catch (Exception ex) { scripted.Add("?" + ex.Message); }
                    Plugin.L.LogInfo($"[wc test] tour {place.Name} (map {place.Map}) scripted: {string.Join(" ", scripted)}");
                    Plugin.L.LogInfo($"[wc test] tour {place.Name} (map {place.Map}): at map {at}, {WcEvents.MainExits - exits} scripts ended, " +
                                     $"{WcEvents.TestStoryShown.Count} lines play would show [{string.Join(" ", WcEvents.TestStoryShown.Take(8))}], " +
                                     $"{WcEvents.TestStorySkipped.Count} skipped, lines seen [{string.Join(" ", lines.Take(10))}]; black {ScreenBlack().Item1}");
                    return true;
                }));
            }
            return plan;
        }

        static List<(float, Func<bool>)> OperaTemplatePlan()
        {
            return new List<(float, Func<bool>)>
            {
                (0f, () => { Check(WcEvents.GotoMap(112, 1), "Opera template: warp to map 112"); return true; }),
                (8f, () =>
                {
                    int map = UserDataManager.Instance()?.CurrentMapId ?? -1;
                    var json = map == 112 ? SaveMapManager.Instance?.ToJson(false) : null;
                    string path = Path.Combine(Plugin.Folder, "opera_map112.json");
                    if (!string.IsNullOrEmpty(json)) File.WriteAllText(path, json);
                    Check(!string.IsNullOrEmpty(json), $"Opera template: native map payload captured on map {map}");
                    return false;
                }),
            };
        }

        static List<(float, Func<bool>)> RoomsPlan()
        {
            const int TreasureRoom = 16, WeaponShop = 9, Basement = 50;
            string Here() => $"map {UserDataManager.Instance()?.CurrentMapId}";
            Func<bool> toTreasure = () => { Check(WcEvents.GotoMap(TreasureRoom, 1), "warp to Narshe's treasure room"); return true; };
            Func<bool> treasure = () =>
            {
                Check(FieldEntity(22) == "+" && FieldEntity(17) == "+",
                      $"the treasure room's locked chest {FieldEntity(22)} and its owner {FieldEntity(17)} ({Here()})");
                Check(WcEvents.GotoMap(WeaponShop, 1), "warp to Narshe's weapon shop");
                return true;
            };
            Func<bool> shop = () =>
            {
                Check(FieldEntity(9) == "+" && FieldEntity(10) != "+",
                      $"Narshe's WoB weapon shop (group 44) {FieldEntity(9)}, not the opening's (18) {FieldEntity(10)} ({Here()})");
                int occupied = WcEvents.Flag(DataStorage.Category.kScenarioFlag1, 50);
                Check(occupied == 0, $"South Figaro not occupied (ScenarioFlag1 50 = {occupied})");
                Check(WcEvents.GotoMap(Basement, 1), "warp to South Figaro's basement");
                return true;
            };
            Func<bool> basement = () =>
            {
                Check(FieldEntity(11) == "+", $"Celes chained in the basement {FieldEntity(11)} ({Here()})");
                return false;
            };
            return new List<(float, Func<bool>)> { (2f, toTreasure), (8f, treasure), (8f, shop), (8f, basement) };
        }

        // the user's Deck, 2026-11-12: down the Ancient Castle library's stairs (map 518's exit to
        // map 519 point 1) the queen's room stayed black. Walked in the way the stairs do, in the
        // World of Ruin with the stairs out (ScenarioFlag2 173); the room must fade in and its map
        // script (sc_map_30811_7: the queen statue, entity 8, cued to 'stone') must end
        static List<(float, Func<bool>)> QueenPlan()
        {
            const int Library = 518, QueenRoom = 519, Queen = 8;
            string Cores()
            {
                var parts = new List<string>();
                try
                {
                    var integ = Integrator.Instance;
                    parts.Add($"script '{integ?.scriptName}', main core alive {integ?.IsMainCoreAlive()}, working {integ?.working}");
                    if (integ?.cores != null)
                        foreach (var c in integ.cores)
                        {
                            var ins = c?.currentInstruction;
                            if (c == null || ins == null) continue;
                            parts.Add($"core {c.coreID} (type {c.type}) at {c.programCounter?.currentAddress}: {ins.mnemonic} " +
                                      string.Join(",", ins.operands?.sValues ?? new string[0]).Trim(','));
                        }
                }
                catch (Exception e) { parts.Add("cores: " + e.Message); }
                return string.Join("; ", parts);
            }
            string Statue()
            {
                try
                {
                    var e = Field()?.GetFieldEntity(Queen);
                    if (e == null) return "no entity 8";
                    int asset = e.Property?.TryCast<Last.Map.PropertyNpc>()?.AssetId ?? -1;
                    return $"entity 8 asset {asset}, shown {e.gameObject.activeInHierarchy}";
                }
                catch (Exception e) { return "statue: " + e.Message; }
            }
            return new List<(float, Func<bool>)>
            {
                (1f, () =>
                {
                    DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 41, 1);   // after the collapse
                    DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 97, 1);   // the Falcon
                    DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 173, 1);  // the library's stairs out
                    Check(WcEvents.GotoMap(Library, 1), "warp to the Ancient Castle's library");
                    return true;
                }),
                (6f, () =>
                {
                    Plugin.L.LogInfo($"[wc test] queen: library map {UserDataManager.Instance()?.CurrentMapId}, black {ScreenBlack().Item1}; {Cores()}");
                    WcEvents.Trace = 200;
                    Check(WcEvents.GotoMap(QueenRoom, 1), "down the library's stairs (map 519 point 1)");
                    return true;
                }),
                (8f, () =>
                {
                    WcEvents.Trace = 0;
                    int map = UserDataManager.Instance()?.CurrentMapId ?? -1;
                    var (black, how) = ScreenBlack();
                    Check(map == QueenRoom, $"in the queen's room: map {map}");
                    Check(!black, $"the queen's room fades in: {how}; {Statue()}; {Cores()}");
                    return false;
                }),
            };
        }

        // the user's playtest (2026-11-06): freeing Celes showed the vanilla scene's lines; WC frees
        // her with none (event/south_figaro.py: the reward, she's gone, done). Talked to here as the
        // action button does (EventProcedure.RequestEntityAction: her own script, sc_e_0079), not
        // started by name: the reward with no question and no story line shown in play
        static List<(float, Func<bool>)> CelesPlan()
        {
            const int Basement = 50, RescueScript = 108;   // script.csv: 108 = sc_e_0079
            Func<bool> warp = () =>
            {
                // the gate (Celes) met, as the check needs
                var gate = WcSeeds.Active?.Checks.TryGetValue("south_figaro", out var c) == true ? c.Gate : 0;
                if (gate > 0) DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, WcEvents.Recruited + gate, 1);
                Check(WcEvents.GotoMap(Basement, 1), "warp to South Figaro's basement");
                return true;
            };
            Func<bool> talk = () =>
            {
                var fc = Field();
                Last.Entity.Field.FieldEntity celes = null;
                int found = -1;
                for (int id = 0; id < 256 && celes == null; id++)
                {
                    Last.Entity.Field.FieldEntity e = null;
                    try { e = fc?.GetFieldEntity(id); } catch (Exception) { }
                    int sid = -1;
                    try { sid = e?.Property?.TryCast<Last.Map.PropertyEvent>()?.ScriptId ?? -1; } catch (Exception) { }
                    if (sid == RescueScript) { celes = e; found = id; }
                }
                Check(celes != null, $"Celes's NPC (script {RescueScript}) in the basement: entity {found}");
                if (celes == null) return false;
                // she looks like the reward (tools/wc_npcs.py: the seed's copy of her map file)
                int want = SeedAsset(CelesFile, CelesObject, VanillaCelesArt), have = -1;
                try { have = celes.Property?.TryCast<Last.Map.PropertyNpc>()?.AssetId ?? -1; } catch (Exception) { }
                Check(have == want, $"Celes's NPC shows the seed's art: asset {have}, the seed's {want}");
                var ep = Object.FindObjectOfType<MainGame>()?.eventProcedure;
                if (ep == null) { Check(false, "no event procedure"); return false; }
                WcEvents.TestMessages.Clear();
                WcEvents.TestStoryShown.Clear();
                WcEvents.TestStorySkipped.Clear();
                WcEvents.TestSelects.Clear();
                var act = ep.RequestEntityAction(celes);
                WcEvents.Running = act != null;
                Plugin.L.LogInfo($"[wc test] talked to Celes (entity {found}): {(act == null ? "no action" : act.ToString())}");
                return true;
            };
            Func<bool> after = () =>
            {
                int rewarded = WcEvents.Flag(DataStorage.Category.kScenarioFlag4, CheckFlags["south_figaro"]);
                var story = WcEvents.TestStoryShown.Where(k => k.StartsWith("E0079")).ToList();
                Check(WcEvents.TestMessages.Count > 0 || WcEvents.TestSelects.Count > 0 || rewarded == 1,
                      $"the rescue ran ({WcEvents.TestMessages.Count} messages, {WcEvents.TestSelects.Count} menus, check flag {rewarded})");
                Check(story.Count == 0, $"no story line shown in play ({story.Count}: {string.Join(" ", story)})");
                Check(WcEvents.TestSelects.Count == 0, $"no question asked (WC has none): {string.Join(" | ", WcEvents.TestSelects)}");
                Check(rewarded == 1, $"the south_figaro check done (flag {rewarded})");
                return false;
            };
            return new List<(float, Func<bool>)> { (2f, warp), (8f, talk), (15f, after) };
        }

        const string CelesFile = "map_20071/Assets/GameAssets/Serial/Res/Map/Map_20071/Map_20071_12/ev_e_0079.json";
        const int CelesObject = 14, VanillaCelesArt = 126;

        // a map object's asset_id in the active seed's copy of a map file (fallback: the seed
        // didn't change that file)
        static int SeedAsset(string rel, int objectId, int fallback)
        {
            var path = Path.Combine(WcSeeds.Active?.Dir ?? "", "files", rel);
            if (!File.Exists(path)) return fallback;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var layer in doc.RootElement.GetProperty("layers").EnumerateArray())
            {
                if (!layer.TryGetProperty("objects", out var objs) || objs.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var o in objs.EnumerateArray())
                {
                    if (!o.TryGetProperty("id", out var id) || id.GetInt32() != objectId) continue;
                    foreach (var p in o.GetProperty("properties").EnumerateArray())
                        if (p.GetProperty("name").GetString() == "asset_id") return p.GetProperty("value").GetInt32();
                }
            }
            return fallback;
        }

        // WC's random Zozo clock (-rc, always on): in the clock's room (map 386) the clock script
        // with the seed's answer sets the quest; the vanilla 6:10:50 doesn't (unless it is the
        // seed's); a clue NPC says WC's line ("It's now ...", N008_C00_390_06_01)
        // WC's (the SNES's) Regen/Sap/Poison ticks (SnesStatusTicks): a test battle with the monsters
        // stopped, Regen on the party's first member, Poison on the second, Sap on the third, Active
        // mode; the plugin's tick record (battle time, slot) against the SNES's clock: Regen and Sap
        // every 8.519 s, Poison 17.039 s; then Haste on the first (12-13 visits: 6.39-6.92 s); then
        // Stop on the first (no Regen until it ends, then on again) while the second is given the
        // Jump status (420: its Poison goes on ticking, as on the SNES). Before: Regen/Sap every 6 s,
        // Poison 16 s, both held while jumping.
        static List<(float, Func<bool>)> StatusTicksPlan()
        {
            const double Regen = 512 / 60.0988, Poison = 1024 / 60.0988;
            const double StepHaste = 32 / 60.0988;
            int savedMode = -1;
            float t0 = 0, phaseB = 0, stopAt = -1, stopEnd = -1, jumpAt = -1, jumpEnd = -1;
            int phase = 0;
            BattlePlayerData[] party = null;
            List<double> Gaps(int slot, string kind, float from, float to)
            {
                var ts = SnesStatusTicks.Log.Where(x => x.Slot == slot && x.Kind == kind && x.Time >= from && x.Time < to).Select(x => (double)x.Time).ToList();
                return ts.Zip(ts.Skip(1), (a, b) => b - a).ToList();
            }
            string Show(List<double> g) => string.Join(" ", g.Select(x => x.ToString("0.000")));
            bool Has(BattleUnitData u, int type) => u?.BattleUnitDataInfo?.Parameter?.CurrentConditionList?.ToArray().Any(c => c != null && c.ConditionType == type) ?? false;
            void Hold()
            {
                var battle = BattlePlugManager.Instance();
                if (battle == null) return;
                var foes = battle.GetEnemyUnits();
                if (foes != null)
                    foreach (var e in foes)
                        if (e != null && !Has(e, 17)) BattleUtility.AddCondition(e, (Last.Defaine.ConditionType)17);
                if (party == null) return;
                foreach (var p in party)
                {
                    var pp = p?.BattleUnitDataInfo?.Parameter;
                    if (pp != null && pp.CurrentHP < pp.ConfirmedMaxHp() / 2) pp.CurrentHP = pp.ConfirmedMaxHp() / 2;
                }
            }
            bool Step()
            {
                var battle = BattlePlugManager.Instance();
                if (battle == null || !battle.IsBattle()) { Check(false, "status_ticks: the battle ended early"); return false; }
                Hold();
                float now = SnesStatusTicks.BattleTime;
                if (phase == 0 && now - t0 >= 45)
                {
                    var r = Gaps(0, "Regen", t0, now);
                    var s = Gaps(2, "Sap", t0, now);
                    var p = Gaps(1, "Poison", t0, now);
                    Check(r.Count >= 3 && r.All(g => Math.Abs(g - Regen) < 0.1), $"status_ticks: Regen every {Regen:0.000} s ({Show(r)})");
                    Check(s.Count >= 3 && s.All(g => Math.Abs(g - Regen) < 0.1), $"status_ticks: Sap every {Regen:0.000} s ({Show(s)})");
                    Check(p.Count >= 1 && p.All(g => Math.Abs(g - Poison) < 0.1), $"status_ticks: Poison every {Poison:0.000} s ({Show(p)})");
                    BattleUtility.AddCondition(party[0], (Last.Defaine.ConditionType)15);      // Haste
                    phaseB = now;
                    phase = 1;
                }
                else if (phase == 1 && now - phaseB >= 40)
                {
                    var r = Gaps(0, "Regen", phaseB + 7, now);
                    Check(r.Count >= 3 && r.All(g => g > 12 * StepHaste - 0.1 && g < 13 * StepHaste + 0.1),
                          $"status_ticks: Haste's Regen every 12-13 visits ({12 * StepHaste:0.00}-{13 * StepHaste:0.00} s: {Show(r)}; SNES mean 6.491)");
                    battle.BattleConditionController?.InterruptRemoveCondition(party[0], (Last.Defaine.ConditionType)15);
                    BattleUtility.AddCondition(party[0], (Last.Defaine.ConditionType)17);      // Stop
                    BattleUtility.AddCondition(party[1], (Last.Defaine.ConditionType)420);     // Jump
                    stopAt = jumpAt = now;
                    Check(Has(party[0], 17) && Has(party[1], 420), $"status_ticks: Stop {Has(party[0], 17)}, Jump {Has(party[1], 420)} added");
                    phase = 2;
                }
                else if (phase == 2)
                {
                    if (jumpEnd < 0 && now - jumpAt >= 20)
                    {
                        var during = SnesStatusTicks.Log.Where(x => x.Slot == 1 && x.Kind == "Poison" && x.Time >= jumpAt && x.Time < now).ToList();
                        Check(during.Count >= 1 && Has(party[1], 420), $"status_ticks: Poison ticks on while jumping ({during.Count} in {now - jumpAt:0.0} s, still jumping {Has(party[1], 420)})");
                        battle.BattleConditionController?.InterruptRemoveCondition(party[1], (Last.Defaine.ConditionType)420);
                        jumpEnd = now;
                    }
                    if (stopEnd < 0 && !Has(party[0], 17)) stopEnd = now;
                    if (stopEnd < 0 && now - stopAt > 90) { Check(false, "status_ticks: Stop still on after 90 s"); battle.BattleConditionController?.InterruptRemoveCondition(party[0], (Last.Defaine.ConditionType)17); stopEnd = now; }
                    if (stopEnd >= 0 && jumpEnd >= 0 && now - stopEnd >= Regen + 2.2)
                    {
                        var during = SnesStatusTicks.Log.Where(x => x.Slot == 0 && x.Kind == "Regen" && x.Time > stopAt + 0.05f && x.Time < stopEnd).ToList();
                        var after = SnesStatusTicks.Log.Where(x => x.Slot == 0 && x.Kind == "Regen" && x.Time >= stopEnd).ToList();
                        Check(during.Count == 0 && after.Count >= 1,
                              $"status_ticks: no Regen while stopped ({stopEnd - stopAt:0.0} s: {during.Count}), on again after ({after.Count}, first {(after.Count > 0 ? after[0].Time - stopEnd : -1):0.00} s on)");
                        Plugin.L.LogInfo($"[wc test] status_ticks: {SnesStatusTicks.Ticks} ticks, {SnesStatusTicks.Steps} steps; log " +
                                         string.Join(" ", SnesStatusTicks.Log.Where(x => !x.Kind.EndsWith("-due")).Select(x => $"{x.Time:0.00}:{x.Slot}{x.Kind[0]}")));
                        var cfg = UserDataManager.Instance()?.Config;
                        if (cfg != null && savedMode >= 0) cfg.BattleModeIndex = savedMode;
                        return false;
                    }
                }
                return true;
            }
            var steps = new List<(float, Func<bool>)>
            {
                (0, () => { WcBattles.Queue(7); Plugin.L.LogInfo("[wc test] status_ticks: party 7 queued"); return true; }),
                (8, () =>
                {
                    var battle = BattlePlugManager.Instance();
                    var cfg = UserDataManager.Instance()?.Config;
                    party = battle?.GetPlayerUnits()?.ToArray().Where(u => u != null).ToArray();
                    if (battle == null || !battle.IsBattle() || cfg == null || party == null || party.Length < 3)
                    { Check(false, $"status_ticks: a battle with 3 party members ({party?.Length ?? 0})"); return false; }
                    Check(SnesStatusTicks.Active, "status_ticks: the plugin's tick clock is on (a WC seed)");
                    savedMode = cfg.BattleModeIndex;
                    cfg.BattleModeIndex = 0;                                                   // Active
                    Hold();
                    BattleUtility.AddCondition(party[0], (Last.Defaine.ConditionType)25);      // Regen
                    BattleUtility.AddCondition(party[1], (Last.Defaine.ConditionType)10);      // Poison
                    BattleUtility.AddCondition(party[2], (Last.Defaine.ConditionType)407);     // Slip (Sap)
                    SnesStatusTicks.Log.Clear();
                    t0 = SnesStatusTicks.BattleTime;
                    Check(Has(party[0], 25) && Has(party[1], 10) && Has(party[2], 407), "status_ticks: Regen, Poison and Sap on the party");
                    return true;
                }),
            };
            for (int i = 0; i < 320; i++) steps.Add((1, Step));
            steps.Add((1, () => { Check(false, "status_ticks: didn't finish in 320 s"); return false; }));
            return steps;
        }

        // WC's (the SNES's) status timers and Jump (SnesStatusTicks, SnesAtb): a test battle, the monsters
        // stopped. (1) Sleep, Reflect, Freeze, Stop on the party's four: each off after 18 / 26 / 34 / 18
        // of the clock's steps (a step 2.13 s; the first comes 1-4 visits after the status is found), not
        // the PR's (Stop measured 31.3 s before). (2) Condemned on the first, as from a level-60 attacker:
        // the SNES's count (19-20 steps, C2/09B4) and the death at its end. (3) The second jumps on auto
        // (Jump dealt into its first slot: SnesAutobattle's Fight-like default): its air time the SNES's (224/256 of a wait
        // timer at half the ATB rate: 10.10 s at Speed 43), not the PR's (measured 16.05 s).
        static List<(float, Func<bool>)> StatusTimersPlan()
        {
            const double Visit = 32 / 60.0988, StepS = 128 / 60.0988;
            int savedMode = -1, phase = 0;
            float t0 = 0, doomAt = -1, jumpFrom = -1;
            int doomLeft = -1;
            BattlePlayerData[] party = null;
            CharacterStatus jumpRow = null;
            bool jumpChecked = false;
            float fightsFrom = 0;
            int fightsSnes = 0;
            int[] jumpSaved = null;
            void RestoreJumpRow()
            {
                if (jumpRow == null || jumpSaved == null) return;
                jumpRow.CommandId1 = jumpSaved[0]; jumpRow.CommandId2 = jumpSaved[1]; jumpRow.CommandId3 = jumpSaved[2];
                jumpRow.CommandId4 = jumpSaved[3]; jumpRow.CommandId5 = jumpSaved[4]; jumpRow.CommandId6 = jumpSaved[5];
                jumpSaved = null;
            }
            var wantOff = new Dictionary<int, (string Name, int Steps)>();
            var offAt = new Dictionary<int, float>();
            bool Has(BattleUnitData u, int type) => u?.BattleUnitDataInfo?.Parameter?.CurrentConditionList?.ToArray().Any(c => c != null && c.ConditionType == type) ?? false;
            bool Dead(BattleUnitData u) => Has(u, 5) || (u?.BattleUnitDataInfo?.Parameter?.CurrentHP ?? 0) <= 0;
            void Hold()
            {
                var battle = BattlePlugManager.Instance();
                if (battle == null) return;
                var foes = battle.GetEnemyUnits();
                if (foes != null)
                    foreach (var e in foes)
                    {
                        if (e == null) continue;
                        if (!Has(e, 17)) BattleUtility.AddCondition(e, (Last.Defaine.ConditionType)17);
                        var ep = e.BattleUnitDataInfo?.Parameter;
                        // a pool no landing or Fight ends in one hit: the battle must outlast the test
                        if (ep != null && ep.CurrentHP > 0 && ep.BaseMaxHp < 30000) ep.BaseMaxHp = 30000;
                        if (ep != null && ep.CurrentHP > 0 && ep.CurrentHP < ep.ConfirmedMaxHp()) ep.CurrentHP = ep.ConfirmedMaxHp();
                    }
                if (party == null) return;
                foreach (var p in party)
                {
                    var pp = p?.BattleUnitDataInfo?.Parameter;
                    if (pp != null && !Dead(p) && pp.CurrentHP < pp.ConfirmedMaxHp() / 2) pp.CurrentHP = pp.ConfirmedMaxHp() / 2;
                }
            }
            List<(float T, string K)> Of(int slot) => SnesStatusTicks.Log.Where(x => x.Slot == slot).Select(x => (x.Time, x.Kind)).ToList();
            bool Step()
            {
                var battle = BattlePlugManager.Instance();
                if (battle == null || !battle.IsBattle()) { Check(false, "status_timers: the battle ended early"); return false; }
                Hold();
                float now = SnesStatusTicks.BattleTime;
                if (phase == 0)
                {
                    foreach (var kv in wantOff)
                    {
                        if (offAt.ContainsKey(kv.Key)) continue;
                        var off = Of(kv.Key).FirstOrDefault(x => x.K == kv.Value.Name + "-off");
                        if (off.K == null) continue;
                        offAt[kv.Key] = off.T;
                        double lo = (kv.Value.Steps - 1) * 4 * Visit, hi = (kv.Value.Steps * 4 + 2) * Visit, got = off.T - t0;
                        int type = kv.Value.Name switch { "Sleep" => 7, "Reflect" => 413, "Freeze" => 603, _ => 17 };
                        Check(got >= lo - 0.1 && got <= hi + 0.1 && !Has(party[kv.Key], type),
                              $"status_timers: {kv.Value.Name} off after {got:0.00} s ({kv.Value.Steps} steps: {lo:0.0}-{hi:0.0} s; still on {Has(party[kv.Key], type)})");
                    }
                    if (offAt.Count == wantOff.Count || now - t0 > 100)
                    {
                        Check(offAt.Count == wantOff.Count, $"status_timers: {offAt.Count} of {wantOff.Count} timed statuses off by {now - t0:0} s");
                        SnesStatusTicks.SetDoomCaster(party[0], 60);
                        BattleUtility.AddCondition(party[0], (Last.Defaine.ConditionType)404);
                        doomAt = now;
                        phase = 1;
                    }
                }
                else if (phase == 1)
                {
                    var set = Of(0).FirstOrDefault(x => x.K.StartsWith("doom-set"));
                    if (doomLeft < 0 && set.K != null)
                    {
                        doomLeft = int.Parse(set.K.Split(' ')[1]);
                        Check(doomLeft == 19 || doomLeft == 20, $"status_timers: Condemned from a level-60 attacker counts {doomLeft} (SNES: 19-20)");
                    }
                    // the game's count reaching 0 (the clock's trace "doom 0/0"): the end on the SNES's clock. At 0 the PR's
                    // DoomConditionFunction.Update kills, or, for a unit that resists the Doom ability (its equipment),
                    // removes Condemned with a 0 and no death (decompiled, tools/decomp/status_timers; 2026-11-20 wc-4:
                    // kittest3's first character resists): both are the game's own end, the timing is the plugin's
                    var zero = Of(0).FirstOrDefault(x => x.K == "doom 0/0");
                    bool resisted = zero.K != null && !Dead(party[0]) && !Has(party[0], 404);
                    if (Dead(party[0]) || resisted || now - doomAt > 70)
                    {
                        double got = set.K == null ? -1 : (zero.K != null ? zero.T : now) - set.T;
                        if (resisted)
                            Plugin.L.LogInfo($"[wc test] status_timers: Condemned's target resists Doom: the game took Condemned off at 0 with no death, as the PR does");
                        else if (!Dead(party[0]))
                            Plugin.L.LogInfo($"[wc test] status_timers: Condemned's target alive after {got:0.0} s: HP {party[0].BattleUnitDataInfo?.Parameter?.CurrentHP}, " +
                                             $"statuses [{string.Join(",", party[0].BattleUnitDataInfo?.Parameter?.CurrentConditionList?.ToArray().Select(c => c?.ConditionType ?? 0) ?? new int[0])}], " +
                                             $"the clock's steps left {SnesStatusTicks.Find(party[0])?.DoomLeft}; trace " +
                                             string.Join(" ", Of(0).Where(x => x.K.StartsWith("doom")).Select(x => $"{x.T:0.00}:{x.K}")));
                        Check((Dead(party[0]) || resisted) && doomLeft > 0 && got >= (doomLeft - 1) * StepS - 0.5 && got <= (doomLeft + 1) * StepS + 1.5,
                              $"status_timers: Condemned's end {got:0.0} s after its count was set ({doomLeft} steps: {doomLeft * StepS:0.0} s; dead {Dead(party[0])}, resisted {resisted})");
                        // (3) Jump: the second jumps on auto, first with the PR's own air time
                        // Jump dealt into its first slot, no Fight (as WC deals it): auto-battle's default is
                        // the first slot's Jump (SnesAutobattle's Fight-like commands), not the PR's Defend
                        var owned1 = party[1].GetOwnedCharacterData();
                        jumpRow = owned1 == null ? null : MasterManager.Instance.GetData<CharacterStatus>(owned1.characterStatusId);
                        if (jumpRow != null)
                        {
                            jumpSaved = new[] { jumpRow.CommandId1, jumpRow.CommandId2, jumpRow.CommandId3, jumpRow.CommandId4, jumpRow.CommandId5, jumpRow.CommandId6 };
                            jumpRow.CommandId1 = 30;
                            if (jumpRow.CommandId2 == 1) jumpRow.CommandId2 = 0;
                            if (jumpRow.CommandId3 == 1) jumpRow.CommandId3 = 0;
                            if (jumpRow.CommandId4 == 1) jumpRow.CommandId4 = 0;
                            if (jumpRow.CommandId5 == 1) jumpRow.CommandId5 = 0;
                            if (jumpRow.CommandId6 == 1) jumpRow.CommandId6 = 0;
                        }
                        var cmds = owned1?.CommandList;
                        if (cmds != null)
                        {
                            var list = new Il2CppSystem.Collections.Generic.List<Command>();
                            list.Add(MasterManager.Instance.GetData<Command>(30));
                            foreach (var c in cmds) if (c != null && c.Id != 1 && c.Id != 30) list.Add(c);
                            owned1.CommandList = list;
                        }
                        Last.UI.BattleUIManager.Instance?.SetIsAuto(true);
                        jumpFrom = now;
                        phase = 2;
                    }
                }
                else if (phase == 2)
                {
                    var j = Of(1).Where(x => x.T >= jumpFrom && (x.K == "jump-up" || x.K == "jump-down")).ToList();
                    var pairs = new List<double>();
                    for (int i = 0; i + 1 < j.Count; i++)
                        if (j[i].K == "jump-up" && j[i + 1].K == "jump-down") pairs.Add(j[i + 1].T - j[i].T);
                    var hp = party[1].BattleUnitDataInfo.Parameter;
                    int speed = hp.ConfirmedAgility();
                    int perTick = (speed + 20) * 96 >> 4;
                    double gauge = 65536.0 / perTick * 2 / 60.0988, snes = 224 * 256.0 / (perTick >> 1) * 2 / 60.0988;
                    if (pairs.Count >= 2 && !jumpChecked)
                    {
                        jumpChecked = true;
                        fightsFrom = now;
                        // the landing's chant is the SNES's air time (SnesAtb.Chanting_Postfix); the PR's own:
                        // 600 / (Agility / 2 + 20) s at the default battle speed. The shortest of two: a landing
                        // whose chant ran out while another fighter acted waits for that act (the game plays one
                        // at a time; 2026-11-20: 12.54 s, then 10.79, 10.79)
                        double want = SnesAtb.SnesJumpSeconds(speed, false, SnesAtb.SnesBattleSpeed(), false, false);
                        double pr = 600.0 / (speed / 2.0 + 20);
                        double air = pairs.Min();
                        Check(Math.Abs(air - want) < 0.5 && SnesAtb.JumpChants > 0,
                              $"status_timers: Jump's air time {air:0.00} s (of {string.Join(" ", pairs.Select(x => x.ToString("0.00")))}; Speed {speed}: SNES {want:0.00} s, 1.75 gauges; the PR's own chant {pr:0.00} s; landings re-timed {SnesAtb.JumpChants})");
                    }
                    if (jumpChecked)
                    {
                        // the command waits: the third's Fights on auto, first with the SNES's wait (C2/067B:
                        // 16, half the ATB's add a move), then with the game's own chant (SnesAtb.CommandWaits
                        // off): its turns come the difference between the two closer together
                        var third = party.Length > 2 ? party[2] : null;
                        var tp = third?.BattleUnitDataInfo?.Parameter;
                        var fights = SnesAtb.ChantLog.Where(x => third != null && x.Unit == third.Pointer && x.Command == 1).ToList();
                        if (SnesAtb.CommandWaits)
                        {
                            if (fights.Count < 4 && now - fightsFrom < 60) return true;
                            SnesAtb.CommandWaits = false;
                            fightsSnes = fights.Count;
                            fightsFrom = now;
                            return true;
                        }
                        if (fights.Count < fightsSnes + 4 && now - fightsFrom < 60) return true;
                        SnesAtb.CommandWaits = true;
                        if (tp != null && fightsSnes >= 3 && fights.Count >= fightsSnes + 3)
                        {
                            int sp = tp.ConfirmedAgility();
                            double wait = SnesAtb.SnesWaitSeconds(16, sp, false, 0, false, false), own = 60 / (sp / 2.0 + 20);
                            List<double> GapsFrom(int from, int to) => Enumerable.Range(from, Math.Max(0, to - from)).Where(i => i + 1 < fights.Count)
                                .Select(i => (double)(fights[i + 1].Time - fights[i].Time)).ToList();
                            var snesGaps = GapsFrom(1, fightsSnes - 1);           // (the first can hold the jump's turn)
                            var ownGaps = GapsFrom(fightsSnes, fights.Count - 1);
                            // the shortest gaps: a turn queued behind another fighter's act comes later (2026-11-20:
                            // 11.29 and 10.47 s with the same wait), never sooner
                            double diff = ownGaps.Min() - snesGaps.Min();
                            Check(Math.Abs(diff - (own - wait)) < 0.35 && fights.Take(fightsSnes).All(f => Math.Abs(f.Wait - wait) < 0.01),
                                  $"status_timers: Fight's wait {wait:0.00} s at Speed {sp} (SNES 16), the game's chant {own:0.00}: turns {string.Join(" ", snesGaps.Select(g => g.ToString("0.00")))} s apart with the SNES's, " +
                                  $"{string.Join(" ", ownGaps.Select(g => g.ToString("0.00")))} with the game's: {diff:0.00} s more (want {own - wait:0.00})");
                        }
                        else Check(false, $"status_timers: the third's Fights on auto ({fightsSnes} with the SNES's wait, {fights.Count - fightsSnes} with the game's)");
                        Last.UI.BattleUIManager.Instance?.SetIsAuto(false);
                        RestoreJumpRow();
                        Plugin.L.LogInfo($"[wc test] status_timers: {SnesStatusTicks.Removed} statuses taken off; log " +
                                         string.Join(" ", SnesStatusTicks.Log.Where(x => !x.Kind.EndsWith("-due")).Select(x => $"{x.Time:0.00}:{x.Slot}{x.Kind}")));
                        var cfg = UserDataManager.Instance()?.Config;
                        if (cfg != null && savedMode >= 0) cfg.BattleModeIndex = savedMode;
                        return false;
                    }
                    if (!jumpChecked && now - jumpFrom > 90)
                    {
                        Check(false, $"status_timers: no two jumps in 90 s ({string.Join(" ", j.Select(x => $"{x.T:0.00}:{x.K}"))})");
                        Last.UI.BattleUIManager.Instance?.SetIsAuto(false);
                        RestoreJumpRow();
                        return false;
                    }
                }
                return true;
            }
            var steps = new List<(float, Func<bool>)>
            {
                (0, () => { WcBattles.Queue(7); Plugin.L.LogInfo("[wc test] status_timers: party 7 queued"); return true; }),
                (8, () =>
                {
                    var battle = BattlePlugManager.Instance();
                    var cfg = UserDataManager.Instance()?.Config;
                    party = battle?.GetPlayerUnits()?.ToArray().Where(u => u != null).ToArray();
                    if (battle == null || !battle.IsBattle() || cfg == null || party == null || party.Length < 3)
                    { Check(false, $"status_timers: a battle with 3 party members ({party?.Length ?? 0})"); return false; }
                    Check(SnesStatusTicks.Active, "status_timers: the plugin's status clock is on (a WC seed)");
                    savedMode = cfg.BattleModeIndex;
                    cfg.BattleModeIndex = 0;                                                   // Active
                    Hold();
                    var add = new[] { (7, "Sleep", 18), (413, "Reflect", 26), (603, "Freeze", 34), (17, "Stop", 18) };
                    for (int i = 0; i < add.Length && i < party.Length; i++)
                    {
                        BattleUtility.AddCondition(party[i], (Last.Defaine.ConditionType)add[i].Item1);
                        wantOff[i] = (add[i].Item2, add[i].Item3);
                    }
                    SnesStatusTicks.Log.Clear();
                    t0 = SnesStatusTicks.BattleTime;
                    Plugin.L.LogInfo($"[wc test] status_timers: Doom pauses on [{string.Join(",", DoomConditionFunction.StopConditions?.ToArray().Select(c => (int)c) ?? new int[0])}]");
                    return true;
                }),
            };
            for (int i = 0; i < 500; i++) steps.Add((1, Step));
            steps.Add((1, () => { Check(false, "status_timers: didn't finish in 500 s"); RestoreJumpRow(); SnesAtb.CommandWaits = true; return false; }));
            return steps;
        }

        // Where a character's turn goes (the user, 2026-11-12: "look into" the ~2.1 s a turn beyond gauge +
        // wait): a test battle, monsters stopped and kept alive, Active mode, auto-battle on; the first
        // party member's gauge, charge (IsChantTimeActDataByUnit) and act (BattleActExection's staging
        // act and state) recorded every frame with the real time and the battle's (SnesStatusTicks). Each
        // turn split into: the fill (0 -> full), full -> its charge starts (the auto-battle's pick), the
        // charge, charge end -> its act starts, the act, the act's end -> the gauge rising again; each in
        // the battle's time (the PR's runs only outside acts) and real time. A record, not a pass/fail.
        // The SNES's (harness, Speed 33, battle-time ticks): fill 206 ticks (6.86 s), Fight's wait 26
        // (0.87 s), its attack 37 ticks over 86 frames (1.43 s real) with time running, the refill from its end.
        static List<(float, Func<bool>)> TurnCyclePlan()
        {
            int savedMode = -1;
            BattlePlayerData me = null;
            // Gt: the game's time (Time.time: the delta the game's chants and gauges count, capped by Unity on a
            // long frame); Bt: the plugin's battle clock (runs only in the battle's update); Dt: the frame's
            // delta as that clock takes it; Left: this unit's chant time left (the game's chantingInfo)
            var rec = new List<(int F, float Real, float Bt, float Gauge, bool Chant, bool Acting, bool Mine, int State, float Gt, float Dt, float Left)>();
            float t0 = 0;
            void Hold()
            {
                var battle = BattlePlugManager.Instance();
                var foes = battle?.GetEnemyUnits();
                if (foes == null) return;
                foreach (var e in foes)
                {
                    if (e == null) continue;
                    if (!(e.BattleUnitDataInfo?.Parameter?.CurrentConditionList?.ToArray().Any(c => c != null && c.ConditionType == 17) ?? false))
                        BattleUtility.AddCondition(e, (Last.Defaine.ConditionType)17);
                    var ep = e.BattleUnitDataInfo?.Parameter;
                    if (ep != null && ep.CurrentHP > 0 && ep.BaseMaxHp < 30000) ep.BaseMaxHp = 30000;
                    if (ep != null && ep.CurrentHP > 0 && ep.CurrentHP < ep.ConfirmedMaxHp()) ep.CurrentHP = ep.ConfirmedMaxHp();
                }
            }
            void Record(int f)
            {
                var b = BattlePlugManager.Instance();
                var prog = b?.BattleProgress?.TryCast<BattleProgressATBFF6>();
                if (prog == null || me == null || b == null || !b.IsBattle()) return;
                var exe = b.BattleActExection;
                var staging = exe?.inStagingBattleActData;
                bool acting = exe != null && exe.GetStatingAct();
                bool mine = acting && staging?.AttackUnitData != null && staging.AttackUnitData.Pointer == me.Pointer;
                float left = 99999f;
                var infos = prog.chantingInfo;
                if (infos != null)
                    for (int k = 0; k < infos.Count; k++)
                    {
                        var info = infos[k];
                        if (info.actData?.AttackUnitData != null && info.actData.AttackUnitData.Pointer == me.Pointer) { left = info.time; break; }
                    }
                rec.Add((f, Time.realtimeSinceStartup, SnesStatusTicks.BattleTime, prog.GetAtbGaugeByUnitData(me),
                         prog.IsChantTimeActDataByUnit(me), acting, mine, exe == null ? -1 : (int)exe.nowState,
                         Time.time, Mathf.Min(Time.deltaTime, 0.25f), left));
            }
            // each interval's slack: the two frames at its ends (a long frame there moves an end by up to its own length; 2026-11-20
            // wc-4: one pick 0.18 s and one charge 0.65 s among six good turns on a busy PC)
            var turns = new List<(double Pick, double Charge, double Restart, double Fill, double Scale, double ActScale, double ActReal,
                                  double PickSlack, double ChargeSlack, double RestartSlack, double FillSlack)>();
            string Analyse()
            {
                var lines = new List<string>();
                turns.Clear();
                // the first turn counted is one whose gauge fills during the recording: a gauge already full when it
                // starts was waiting for auto-battle to switch on (2026-11-20 wc-6: a first pick of 0.40 s, then 0.02-0.05)
                int i = Math.Max(0, rec.FindIndex(r => r.Gauge < 99.9f));
                while (true)
                {
                    int full = rec.FindIndex(i, r => r.Gauge >= 99.9f);
                    if (full < 0) break;
                    int chant = rec.FindIndex(full, r => r.Chant);
                    int act = rec.FindIndex(full, r => r.Mine);
                    if (act < 0) break;
                    int actEnd = rec.FindIndex(act, r => !r.Mine);
                    if (actEnd < 0) break;
                    int rise = rec.FindIndex(actEnd, r => r.Gauge > 0.5f && r.Gauge < 99.9f);
                    int next = rise < 0 ? -1 : rec.FindIndex(rise, r => r.Gauge >= 99.9f);
                    // intervals in the game's time (Gt), as its chants and gauges count (2026-11-20: the plugin's clock
                    // drops part of a long frame, so a busy PC made them look off)
                    string Bt(int a, int b) => a < 0 || b < 0 ? "?" : $"{rec[b].Gt - rec[a].Gt:0.00}";
                    string Re(int a, int b) => a < 0 || b < 0 ? "?" : $"{rec[b].Real - rec[a].Real:0.00}";
                    // the charge ends when the chant's time runs out; an act waiting after that for another
                    // fighter's act to finish (the game plays one at a time) isn't charging (2026-11-20: turns
                    // queued behind an attack read 2.5-2.8 s)
                    int chantEnd = chant < 0 ? -1 : rec.FindIndex(chant, r => !r.Chant || r.Left <= 0f);
                    var states = string.Join(",", rec.Skip(act).Take(Math.Max(0, actEnd - act)).Select(r => r.State).Distinct());
                    // the plugin's battle clock against the frames' own deltas: 1.0 = it ran every frame
                    double Clock(int a, int b)
                    {
                        double sum = 0;
                        for (int k = a + 1; k <= b; k++) sum += rec[k].Dt;
                        return (rec[b].Bt - rec[a].Bt) / Math.Max(1e-3, sum);
                    }
                    if (chant >= 0 && chantEnd >= 0 && rise >= 0 && next >= 0)
                    {
                        turns.Add((rec[chant].Gt - rec[full].Gt, rec[chantEnd].Gt - rec[chant].Gt, rec[rise].Gt - rec[actEnd].Gt,
                                   rec[next].Gt - rec[rise].Gt, Clock(full, next), Clock(act, actEnd), rec[actEnd].Real - rec[act].Real,
                                   rec[full].Dt + rec[chant].Dt, rec[chant].Dt + rec[chantEnd].Dt, rec[actEnd].Dt + rec[rise].Dt, rec[rise].Dt + rec[next].Dt));
                    }
                    lines.Add($"turn: full->charge {Bt(full, chant)} (real {Re(full, chant)}), charge {Bt(chant, chantEnd)} (real {Re(chant, chantEnd)}), " +
                              $"charge end->act {Bt(chantEnd, act)} (real {Re(chantEnd, act)}), act {Bt(act, actEnd)} (real {Re(act, actEnd)}, states {states}), " +
                              $"act end->rising {Bt(actEnd, rise)} (real {Re(actEnd, rise)}), fill {Bt(rise, next)} (real {Re(rise, next)}); full->full {Bt(full, next)} (real {Re(full, next)})");
                    if (next < 0) break;
                    i = next + 1;
                    while (i < rec.Count && rec[i].Gauge >= 99.9f) i++;
                }
                return string.Join("\n", lines);
            }
            return new List<(float, Func<bool>)>
            {
                (0, () => { WcBattles.Queue(7); Plugin.L.LogInfo("[wc test] turn_cycle: party 7 queued"); return true; }),
                (8, () =>
                {
                    var battle = BattlePlugManager.Instance();
                    var cfg = UserDataManager.Instance()?.Config;
                    me = battle?.GetPlayerUnits()?.ToArray().FirstOrDefault(u => u != null);
                    if (battle == null || !battle.IsBattle() || cfg == null || me == null) { Check(false, "turn_cycle: a battle"); return false; }
                    savedMode = cfg.BattleModeIndex;
                    cfg.BattleModeIndex = 0;                                                   // Active
                    Hold();
                    Last.UI.BattleUIManager.Instance?.SetIsAuto(true);
                    SnesStatusTicks.FrameHook = Record;
                    t0 = SnesStatusTicks.BattleTime;
                    var p = me.BattleUnitDataInfo.Parameter;
                    Plugin.L.LogInfo($"[wc test] turn_cycle: recording {me.GetOwnedCharacterData()?.characterStatusId} (Agility {p.ConfirmedAgility()}: " +
                                     $"a gauge {100 / SnesAtb.SnesRate(p.ConfirmedAgility(), false, 0, false, false):0.00} s, Fight's wait {SnesAtb.SnesWaitSeconds(16, p.ConfirmedAgility(), false, 0, false, false):0.00} s)");
                    return true;
                }),
                (15, () => { Hold(); return true; }), (15, () => { Hold(); return true; }), (15, () => { Hold(); return true; }), (15, () => { Hold(); return true; }),
                (15, () => { Hold(); return true; }),
                (5, () =>
                {
                    SnesStatusTicks.FrameHook = null;
                    Last.UI.BattleUIManager.Instance?.SetIsAuto(false);
                    var cfg = UserDataManager.Instance()?.Config;
                    if (cfg != null && savedMode >= 0) cfg.BattleModeIndex = savedMode;
                    string a = Analyse();
                    Plugin.L.LogInfo($"[wc test] turn_cycle: {rec.Count} frames, battle time {SnesStatusTicks.BattleTime - t0:0.0} s\n{a}");
                    var mp = me.BattleUnitDataInfo.Parameter;
                    int sp = mp.ConfirmedAgility();
                    double gauge = 100 / SnesAtb.SnesRate(sp, false, 0, false, false), wait = SnesAtb.SnesWaitSeconds(16, sp, false, 0, false, false);
                    Check(turns.Count >= 3, $"turn_cycle: {turns.Count} whole turns recorded");
                    if (turns.Count > 0)
                    {
                        Check(turns.All(t => t.Pick < 0.1 + t.PickSlack && t.Restart < 0.1 + t.RestartSlack),
                              $"turn_cycle: the auto-battle's pick and the gauge's restart take no time (picks {string.Join(" ", turns.Select(t => t.Pick.ToString("0.00")))}, restarts {string.Join(" ", turns.Select(t => t.Restart.ToString("0.00")))})");
                        Check(turns.All(t => Math.Abs(t.Charge - wait) < 0.06 + t.ChargeSlack && Math.Abs(t.Fill - gauge) < 0.12 + t.FillSlack),
                              $"turn_cycle: each charge the SNES's Fight wait {wait:0.00} s ({string.Join(" ", turns.Select(t => t.Charge.ToString("0.00")))}), each fill a SNES gauge {gauge:0.00} s ({string.Join(" ", turns.Select(t => t.Fill.ToString("0.00")))})");
                        Check(turns.All(t => Math.Abs(t.ActScale - 1) < 0.08 && Math.Abs(t.Scale - 1) < 0.08),
                              $"turn_cycle: the battle's time runs through the attack, as on the SNES (the battle clock over the frames' time, 1 = every frame: over a turn {string.Join(" ", turns.Select(t => t.Scale.ToString("0.00")))}, " +
                              $"over the attack {string.Join(" ", turns.Select(t => t.ActScale.ToString("0.00")))}; the attack {string.Join(" ", turns.Select(t => t.ActReal.ToString("0.00")))} s real, the SNES's 1.43)");
                    }
                    return false;
                }),
            };
        }

        static List<(float, Func<bool>)> ClockPlan()
        {
            const int ClockRoom = 386, ZozoClockQuest = 70 + 6;
            int Quest() => WcEvents.Flag(DataStorage.Category.kScenarioFlag4, ZozoClockQuest);
            var seed = WcSeeds.Active;
            bool vanillaIsRight = seed.Clock.SequenceEqual(new[] { 2, 0, 4 });
            Func<bool> warp = () => { Check(WcEvents.GotoMap(ClockRoom, 1), "warp to the Zozo clock's room"); return true; };
            Func<bool> wrong = () =>
            {
                foreach (var a in new[] { 2, 0, 4 }) WcEvents.TestAnswers.Enqueue(a);
                Check(WcEvents.RunScript("sc_e_0299"), "the clock answered 6:10:50 (vanilla's)");
                return true;
            };
            Func<bool> right = () =>
            {
                Check(vanillaIsRight || Quest() == 0, $"6:10:50 doesn't set a {seed.ClockTime} clock: quest flag {Quest()}; {WcEvents.LastClock}");
                foreach (var a in seed.Clock) WcEvents.TestAnswers.Enqueue(a);
                Check(WcEvents.RunScript("sc_e_0299"), $"the clock answered {seed.ClockTime} (the seed's: options {string.Join(" ", seed.Clock)})");
                return true;
            };
            Func<bool> done = () =>
            {
                Check(Quest() == 1, $"{seed.ClockTime} sets the clock: quest flag {Quest()}; {WcEvents.LastClock}");
                var clue = MessageManager.Instance?.GetMessage("N008_C00_390_06_01", false) ?? "";
                Check(clue.StartsWith("It's now "), $"a clue NPC says WC's line: '{clue}'");
                // the story-text rule (WcEvents.SkipStoryLine): the clock's question and its "keeps
                // ticking" answer have no speaker, so play shows them
                Check(WcEvents.TestStoryShown.Contains("E0299_00_386_a_01") && WcEvents.TestStoryShown.Contains("E0299_00_386_a_04") &&
                      !WcEvents.TestStorySkipped.Any(k => k.StartsWith("E0299")),
                      $"the clock's lines stay in play (shown: {string.Join(" ", WcEvents.TestStoryShown.Where(k => k.StartsWith("E0299")))})");
                return false;
            };
            return new List<(float, Func<bool>)> { (2f, warp), (6f, wrong), (10f, right), (12f, done) };
        }

        // WC's -pd / -nee metamorph sets (port_procs.metamorph: battle_extra_data's
        // monster_exchange_item): in a battle the game's own data has the seed's sets (set 1 without
        // Fenix Down, content 10; set 18 without Rename Card... content 327)
        static List<(float, Func<bool>)> MorphPlan()
        {
            Func<bool> start = () =>
            {
                var controller = Object.FindObjectOfType<FieldMap>()?.fieldController;
                var party = MasterManager.Instance.GetData<MonsterParty>(7);
                if (controller == null || party == null) { Check(false, "morph: no field controller"); return false; }
                controller.Last_Map_IMapEncounter_ExcuteEncount(party, party.BattleBackgroundAssetId);
                return true;
            };
            Func<bool> look = () =>
            {
                var desc = BattlePlugManager.Instance()?.BattleExtraDescController?.desc;
                var list = desc?.monster_exchange_item;
                if (list == null) { Check(false, "morph: no battle extra data in the battle"); return false; }
                var flags = WcSeeds.Active?.Flags.Split(' ') ?? new string[0];
                int fenix = 0, rename = 0, n = 0;
                foreach (var e in list)
                {
                    if (e?.exchange_content_ids == null) continue;
                    n++;
                    foreach (var c in e.exchange_content_ids) { if (c == 10) fenix++; if (c == 327) rename++; }
                }
                Check(n > 200, $"metamorph sets in the battle: {n} monsters");
                if (flags.Contains("-pd")) Check(fenix == 0, $"-pd: no Fenix Down (content 10) from Metamorph: {fenix} left");
                if (flags.Contains("-nee")) Check(rename == 0, $"-nee: no content 327 from Metamorph: {rename} left");
                return false;
            };
            return new List<(float, Func<bool>)> { (2f, start), (8f, look) };
        }

        // WC's -saw: the Atma Weapon's attack, as the game calculates it (FunctionBase.MainCalc of
        // the Attack ability's physical function, the BNW probe's way), in a test battle, with the
        // option off and on: the damage doubles (the divisor 64 -> 32)
        static List<(float, Func<bool>)> AtmaPlan()
        {
            Func<bool> start = () =>
            {
                var controller = Object.FindObjectOfType<FieldMap>()?.fieldController;
                var party = MasterManager.Instance.GetData<MonsterParty>(7);
                if (controller == null || party == null) { Check(false, "atma: no field controller"); return false; }
                controller.Last_Map_IMapEncounter_ExcuteEncount(party, party.BattleBackgroundAssetId);
                return true;
            };
            Func<bool> swing = () =>
            {
                var battle = BattlePlugManager.Instance();
                if (battle == null || !battle.IsBattle()) { Check(false, "atma: no battle"); return false; }
                var provider = Last.Management.BattleControllerManagement.Instance()?.GetCurrentProvider()?.TryCast<Last.Systems.CalcControllerProvider>();
                var player = battle.GetPlayerUnits()?.FirstOrDefault(p => p != null);
                var enemy = battle.GetEnemyUnits()?.FirstOrDefault(e => e != null);
                var mm = MasterManager.Instance;
                var atma = mm.GetData<Weapon>(WcChallenges.AtmaWeapon);
                var attack = mm.GetData<Ability>(1);
                if (provider == null || player == null || enemy == null || atma == null || attack == null)
                { Check(false, "atma: no provider / units / weapon"); return false; }
                var ap = player.BattleUnitDataInfo.Parameter;
                var tp = enemy.BattleUnitDataInfo.Parameter;
                double Average(bool saw)
                {
                    WcChallenges.SawOverride = saw;
                    long sum = 0; int n = 0;
                    for (int i = 0; i < 24; i++)
                    {
                        var info = new Last.Systems.CalcInfo();
                        info.command = mm.GetData<Command>(1);
                        info.isDominantArm = true;
                        info.targetSum = 1;
                        info.baseHitRate = 255;
                        info.userWeapon = atma;
                        info.userRightWeapon = atma;
                        info.userCharaData = player.ownedCharacterData;
                        info.targetMonster = enemy.GetMonster();
                        provider.SetCalcInfo(info);
                        var f = Last.Battle.Function.ParameterActFunctionManagment.GetFunctionBaseCalc(
                            (Last.Defaine.Master.FunctionType)2, 0, 0, 0, Last.Battle.BattleActData.TargetType.SingleEnemy);
                        if (f == null) break;
                        f.MainCalc(ap, tp, attack, null);
                        var r = f.GetCalcResult();
                        if (r == null || r.GetHitType() != Last.Systems.HitType.Hit) continue;
                        sum += r.GetValue(false);
                        n++;
                    }
                    WcChallenges.SawOverride = null;
                    return n == 0 ? 0 : (double)sum / n;
                }
                double off = Average(false), on = Average(true);
                Check(off > 0 && on > off * 1.6 && on < off * 2.4,
                      $"-saw: Atma Weapon attack averages {off:0.0} without, {on:0.0} with ({(off > 0 ? on / off : 0):0.00}x); {WcChallenges.LastAtma}");
                return false;
            };
            return new List<(float, Func<bool>)> { (2f, start), (8f, swing) };
        }

        static List<Learning> LearningRows()
        {
            var rows = new List<Learning>();
            foreach (var kv in MasterManager.Instance.GetList<Learning>()) if (kv.Value != null) rows.Add(kv.Value);
            return rows;
        }

        // WC's natural magic (WcSpells): a recruit knows the seed's natural magic up to its level
        // and nothing else (-scan / -warp / a completed "Learn Spells" aside). Empty lists = right.
        static (List<int> Unearned, List<int> Missing, List<int> Known) SpellState(Last.Data.User.OwnedCharacterData c)
        {
            var natural = WcSpells.Natural(c, c.Parameter.ConfirmedLevel());
            var allowed = new HashSet<int>(natural);
            allowed.UnionWith(WcSpells.Granted());
            var known = new List<int>();
            if (c.OwnedAbilityList != null)
                foreach (var a in c.OwnedAbilityList)
                    if (a?.Ability != null && WcSpells.IsSpell(a.Ability.Id) && a.SkillLevel > 0) known.Add(a.Ability.Id);
            return (known.Where(id => !allowed.Contains(id)).ToList(), natural.Where(id => !WcSkills.Knows(c, id)).OrderBy(x => x).ToList(), known);
        }

        static string SpellText(Last.Data.User.OwnedCharacterData c)
        {
            var (unearned, missing, known) = SpellState(c);
            return $"{c.characterStatusId} (L{c.Parameter.ConfirmedLevel()}, job {c.JobId}): knows [{string.Join(" ", known.OrderBy(x => x))}]" +
                   (unearned.Count > 0 ? $", not its natural magic [{string.Join(" ", unearned)}]" : "") +
                   (missing.Count > 0 ? $", missing [{string.Join(" ", missing)}]" : "");
        }

        // Every character recruited (all 14, in turn), each checked as it joins; then a save
        // recruited before WcSpells: Terra given back the PR's Cure / Fire, the field repair takes
        // what no owned esper / gear / natural magic teaches, and keeps a spell an owned esper does
        static List<(float, Func<bool>)> SpellsPlan()
        {
            var plan = new List<(float, Func<bool>)>();
            Last.Data.User.OwnedCharacterData Char(int id) =>
                UserDataManager.Instance().OwnedCharacterList.ToArray().FirstOrDefault(c => c != null && c.characterStatusId == id);
            // spells=<level> above 1: the starters raised to it first, so -sal's recruits join there
            // (what the PR builds in at a higher level; the user's Celes knew several spells)
            int raise = int.TryParse(req.GetValueOrDefault("spells", "1"), out var lvl) ? lvl : 1;
            plan.Add((1, () =>
            {
                if (raise > 1)
                    for (int id = 1; id <= 14; id++)
                        if (WcEvents.IsRecruited(id) && Char(id) is var st && st != null) WcRecruits.RaiseTo(st, raise);
                var bad = new List<string>();
                for (int id = 1; id <= 14; id++)
                {
                    if (!WcEvents.IsRecruited(id)) WcEvents.Join(id);
                    var c = Char(id);
                    if (c == null) { bad.Add($"{id} missing"); continue; }
                    var (unearned, missing, _) = SpellState(c);
                    string line = SpellText(c);
                    Plugin.L.LogInfo("[wc test]   spells " + line);
                    if (unearned.Count > 0 || missing.Count > 0) bad.Add(line);
                }
                Check(bad.Count == 0, $"spells: every recruit knows exactly its natural magic{(bad.Count > 0 ? ": " + string.Join("; ", bad) : "")}");
                var terra = Char(1);
                if (terra != null)
                {
                    var natural = WcSpells.Natural(terra, terra.Parameter.ConfirmedLevel());
                    bool cure = WcSkills.Knows(terra, 31), fire = WcSkills.Knows(terra, 40);
                    Check(cure == natural.Contains(31) && fire == natural.Contains(40),
                          $"spells: Terra knows Cure {cure} / Fire {fire}, as the seed's natural magic says ({natural.Contains(31)} / {natural.Contains(40)})");
                }
                return true;
            }));
            plan.Add((1, () =>
            {
                // every character the PR builds spells into (WcSpells.BuiltIn), as an old save has it
                var udm = UserDataManager.Instance();
                var mm = MasterManager.Instance;
                var stones = udm.OwnedMagicStoneList;
                var rows = LearningRows();
                var granted = WcSpells.Granted();
                bool Teaches(Learning l, int spell) => mm.GetData<Content>(l.ContentId)?.TypeValue == spell;
                foreach (var kv in WcSpells.BuiltIn)
                {
                    var c = Char(kv.Key);
                    if (c == null) { Check(false, $"spells: no character {kv.Key}"); continue; }
                    var natural = WcSpells.Natural(c, c.Parameter.ConfirmedLevel());
                    foreach (var id in kv.Value) WcSkills.Teach(c, id);
                    WcSpells.RepairBuiltIns();
                    var report = new List<string>();
                    bool ok = true;
                    foreach (var id in kv.Value)
                    {
                        bool keep = natural.Contains(id) || granted.Contains(id) ||
                                    rows.Any(l => (l.TypeId == 4 || (l.TypeId == 2 && stones.Contains(l.Value1))) && Teaches(l, id));
                        bool has = WcSkills.Knows(c, id);
                        ok &= has == keep;
                        report.Add($"{id} {has} (keep {keep})");
                    }
                    Check(ok, $"spells: an old save's character {kv.Key} with the PR's built-in spells keeps only what the seed could teach: {string.Join(", ", report)}");
                    // owning an esper that teaches one: the character may have learned it there, so it stays
                    int spell = kv.Value[0];
                    int esper = rows.Where(l => l.TypeId == 2 && Teaches(l, spell)).Select(l => l.Value1).FirstOrDefault();
                    if (esper > 0 && !stones.Contains(esper) && !natural.Contains(spell) && !granted.Contains(spell))
                    {
                        WcSkills.Teach(c, spell);
                        stones.Add(esper);
                        WcSpells.RepairBuiltIns();
                        Check(WcSkills.Knows(c, spell), $"spells: character {kv.Key}'s {spell} kept while esper {esper} (teaches it) is owned");
                        stones.Remove(esper);
                        WcSpells.RepairBuiltIns();
                        bool gear = rows.Any(l => l.TypeId == 4 && Teaches(l, spell));
                        Check(WcSkills.Knows(c, spell) == gear, $"spells: without that esper, character {kv.Key}'s {spell} goes again ({WcSkills.Knows(c, spell)})");
                    }
                }
                return true;
            }));
            return plan;
        }

        // the option batch of 2026-11-06 (needs a seed with -sal -srr/-slr/-sdr -rnl -eer -eu -stra
        // -dda -ari -scan -frw): each in place, as the game would call it
        static List<(float, Func<bool>)> FeaturePlan()
        {
            var s = WcSeeds.Active;
            int boxParty = 0;
            Func<bool> run = () =>
            {
                var udm = UserDataManager.Instance();
                var owned = new List<Last.Data.User.OwnedCharacterData>();
                foreach (var c in udm.OwnedCharacterList) if (c != null && c.characterStatusId >= 1 && c.characterStatusId <= 14) owned.Add(c);
                var party = udm.GetPartyMemberData().ToArray();
                // start level (-stl, the port's character_status)
                Check(party.All(c => c.Parameter.BaseLevel >= 3), $"start levels [{string.Join(" ", party.Select(c => $"{c.characterStatusId}:{c.Parameter.BaseLevel}"))}]");
                // -sal: someone joins at the recruited characters' average
                var lead = owned.First(c => c.characterStatusId == party[0].characterStatusId);   // the live data (the party list is copies)
                WcRecruits.RaiseTo(lead, 20);
                var recruited = owned.Where(c => WcEvents.IsRecruited(c.characterStatusId)).ToList();
                int newcomer = Enumerable.Range(1, 12).FirstOrDefault(id => !WcEvents.IsRecruited(id));
                int want = recruited.Sum(c => c.Parameter.ConfirmedLevel()) / Math.Max(1, recruited.Count);
                if (newcomer > 0)
                {
                    WcEvents.Join(newcomer);
                    Last.Data.User.OwnedCharacterData joined = null;
                    foreach (var c in udm.OwnedCharacterList) if (c?.characterStatusId == newcomer) joined = c;
                    Check(joined != null && joined.Parameter.ConfirmedLevel() == want,
                          $"-sal: character {newcomer} joined at level {joined?.Parameter.ConfirmedLevel()} (average {want}); {WcRecruits.LastJoin}");
                    var start = MasterManager.Instance.GetData<Last.Data.Master.CharacterStatus>(newcomer);
                    Check(joined != null && start != null && (want <= start.Lv || joined.Parameter.ConfirmedMaxHp() > start.Hp),
                          $"-sal: character {newcomer}'s level {want} includes its HP growth: {joined?.Parameter.CurrentHP}/{joined?.Parameter.ConfirmedMaxHp()} (start {start?.Hp}); {WcRecruits.LastJoin}");
                }
                // -srr / -slr / -sdr: the start skills with their command holders
                WcRages.StartSkills();
                foreach (var (kind, command) in new[] { ("rages", 23), ("lores", 17), ("dances", 22) })
                {
                    if (!s.StartSkills.TryGetValue(kind, out var list)) continue;
                    var holder = WcSkills.Holder(command);
                    if (holder == null) { Plugin.L.LogInfo($"[wc test] {kind}: no holder of command {command} yet"); continue; }
                    int known = list.Count(id => WcSkills.Knows(holder, id));
                    Check(known == list.Count, $"start {kind}: character {holder.characterStatusId} knows {known} of {list.Count}");
                }
                // -rnl: a won battle's enemies become rages (a holder, or later)
                WcRages.BattleStart(new[] { 2, 3 });
                WcRages.BattleWon();
                var rager = WcSkills.Holder(23);
                if (rager != null) Check(WcSkills.Knows(rager, 801) && WcSkills.Knows(rager, 802), $"-rnl: rages 801/802 learned by {rager.characterStatusId}; {WcRages.LastRage}");
                // -eer: an esper the seed keeps from someone
                if (s.EsperEquip.Count > 0)
                {
                    var c = lead;
                    var stone = s.EsperEquip.FirstOrDefault(kv => !kv.Value.Contains(c.characterStatusId)).Key;
                    if (stone > 0)
                    {
                        new Last.Management.OwnedCharacterClient().SetMagicStone(c.Id, stone);
                        Check(c.MagicStoneId != stone, $"-eer: character {c.characterStatusId} can't equip magicite {stone} (equipped {c.MagicStoneId})");
                    }
                }
                // Magic, as in WC's SNES (everyone but Gogo/Umaro keeps it): nobody gets it for
                // nothing, a character who knows a spell (or holds magicite) has it; the PR's own
                // EquipMagicStone adds it on an equip and is no longer hooked
                var noMagic = owned.FirstOrDefault(c => c.characterStatusId >= 1 && c.characterStatusId <= 13 && !WcEspers.HasCommand(c, 7) &&
                                                        !WcEspers.HasCommand(c, 31) && c.MagicStoneId <= 0 && !WcEspers.KnowsSpell(c));
                if (noMagic != null)
                {
                    WcEspers.RestoreMagicCommands();
                    Check(!WcEspers.HasCommand(noMagic, 7), $"magicite command: character {noMagic.characterStatusId} gets no Magic without magicite or spells");
                    Last.Data.User.OwnedAbility cure = null;
                    foreach (var a in noMagic.OwnedAbilityList) if (a?.Ability != null && a.Ability.Id == 31) cure = a;
                    int cureBefore = cure?.SkillLevel ?? -1;
                    WcSkills.Teach(noMagic, 31);
                    int restored = WcEspers.RestoreMagicCommands();
                    Check(restored >= 1 && WcEspers.HasCommand(noMagic, 7),
                          $"magicite command: character {noMagic.characterStatusId} knows Cure and gets Magic ({restored} restored)");
                    foreach (var a in noMagic.OwnedAbilityList) if (a?.Ability != null && a.Ability.Id == 31) a.SkillLevel = Math.Max(cureBefore, 0);
                    var undo = new Last.Management.OwnedCharacterClient();
                    undo.RemoveCommand(noMagic.Id, 7);
                    undo.UpdateChangeCommandForAccessory(noMagic.Id);
                }
                // -eu: Umaro's equip menu changes every slot (the game's own question, as its menus ask it;
                // check=eu_equip has the rest)
                var relicsOnly = WcRecruits.MenuOnlyRelics(14);
                Check(relicsOnly == !s.Flags.Split(' ').Contains("-eu"), $"-eu: Umaro's equip menu relics only: {relicsOnly} ({WcRecruits.EquipState})");
                // -stra and WC's item bits: the Runic / Bushido lists
                Check(WcItems.LastWeapons != "" && (!s.Flags.Split(' ').Contains("-stra") || WcItems.LastWeapons.Contains("90")),
                      $"Runic/Bushido weapons: {WcItems.LastWeapons}");
                // -dda: a dance's help lists its steps
                Last.Data.Master.Content dance = null;
                foreach (var kv in MasterManager.Instance.GetList<Last.Data.Master.Content>())
                    if (kv.Value != null && kv.Value.TypeId == 4 && kv.Value.TypeValue == 164) dance = kv.Value;
                var help = dance == null ? null : MessageManager.Instance?.GetMessage(dance.MesIdDescription, false);
                Check(!s.Flags.Split(' ').Contains("-dda") || (help != null && help.Contains(" / ")), $"-dda: Wind Rhapsody's help '{help}'");
                // -ari: the four sales
                Check(!s.Flags.Split(' ').Contains("-ari") || s.AuctionItems.Count >= 3, $"-ari: {s.AuctionItems.Count} auction sales changed");
                // -scan: monster flag 1 (can't scan) answers no
                bool scanBlocked = Last.Systems.MonsterFlagGroupUtility.IsMatcthingFlag(1, 1);
                Check(scanBlocked == !s.Flags.Split(' ').Contains("-scan"), $"-scan: group 1's can't-scan flag reads {scanBlocked}");
                // esper bonuses: the seed's
                Check(s.EsperBonusVanilla.Count == 27, $"esper bonuses: {s.EsperBonus.Count} changed of {s.EsperBonusVanilla.Count}");
                Check(s.Flags.Contains("-csb") ? s.CursedShieldBattles < 256 : s.CursedShieldBattles == 256, $"-csb: the cursed shield uncurses after {s.CursedShieldBattles} battles");
                if (s.MonsterChests.Count == 0) return false;
                // -chrm: every box's party exists with monsters (2026-11-21: ~10% of WC's packs had no PR party;
                // seed_info now builds or rerolls them)
                var noParty = s.MonsterChests.Where(kv =>
                {
                    var mp = MasterManager.Instance.GetData<MonsterParty>(kv.Value);
                    return mp == null || new[] { mp.Monster1, mp.Monster2, mp.Monster3, mp.Monster4, mp.Monster5, mp.Monster6, mp.Monster7, mp.Monster8, mp.Monster9 }.All(x => x <= 0);
                }).Select(kv => $"{kv.Key}->{kv.Value}").ToList();
                Check(noParty.Count == 0, $"-chrm: all {s.MonsterChests.Count} monster boxes have a party with monsters{(noParty.Count > 0 ? ": missing " + string.Join(" ", noParty) : "")}");
                // a monster box's battle, queued as opening the chest does, starts on its own: the highest party id,
                // which is a party the seed made from its formation when there is one (ids after the game's own)
                boxParty = s.MonsterChests.Values.Max();
                WcBattles.Queue(boxParty);
                return true;
            };
            Func<bool> box = () =>
            {
                var b = BattlePlugManager.Instance();
                var party = MasterManager.Instance.GetData<MonsterParty>(boxParty);
                var expect = party == null ? new List<int>() : new[] { party.Monster1, party.Monster2, party.Monster3, party.Monster4, party.Monster5, party.Monster6, party.Monster7, party.Monster8, party.Monster9 }.Where(x => x > 0).ToList();
                var ids = b?.GetEnemyUnits()?.Select(e => e?.GetMonster()?.Id ?? 0).Where(i => i > 0).ToList() ?? new List<int>();
                Check(b != null && b.IsBattle() && ids.Count > 0 && ids.All(expect.Contains),
                      $"-chrm: the monster box's battle started: monsters [{string.Join(" ", ids)}] (party {boxParty}: [{string.Join(" ", expect)}])");
                return false;
            };
            return new List<(float, Func<bool>)> { (2f, run), (10f, box) };
        }

        static List<(float, Func<bool>)> TrackPlan()
        {
            Last.UI.KeyInput.MainMenuController menu = null;
            return new List<(float, Func<bool>)>
            {
                (0f, () =>
                {
                    var list = new Il2CppSystem.Collections.Generic.List<Last.Data.CommandMenuContentData>();
                    foreach (var id in new[] { 1, 2, 3, 4, 5, 7, 9 })
                        list.Add(new Last.Data.CommandMenuContentData((Last.Defaine.MenuCommandId)id, "MENU_X"));
                    WcTrack.Initalize_Prefix(list);
                    WcTrack.Initalize_Prefix(list);   // a second build of the same list: still one entry
                    var ids = new List<int>();
                    for (int i = 0; i < list.Count; i++) ids.Add((int)list[i].Id);
                    var want = WcSeeds.IronMog() ? "1,2,3,4,5,100,7" : "1,2,3,4,5,100,7,9";   // -nosaves: no Save
                    Check(string.Join(",", ids) == want, $"track entry above Config{(WcSeeds.IronMog() ? ", no Save (-nosaves)" : "")}: [{string.Join(",", ids)}]");
                    var text = MessageManager.Instance?.GetMessageByMessageConclusion("MENU_WC_TRACK");
                    Check(text == "Track", $"track entry's label '{text}'");
                    int F4(int i) => WcEvents.Flag(DataStorage.Category.kScenarioFlag4, i);
                    int keep = F4(1);
                    DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, 1, 1);   // Tzen done
                    int done = WcTrack.Checks.Count(WcTrack.Done);
                    DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, 1, keep);
                    Check(WcTrack.Checks.Count == 62 && done == 1, $"track list: {WcTrack.Checks.Count} checks, {WcTrack.Ported} ported, {done} done with Tzen's flag");
                    Check(!WcTrack.Pick((Last.Defaine.MenuCommandId)100) && WcScreen.Open && WcScreen.Track, "picking Track opens the Track pages");
                    var pages = WcScreen.TrackTest();
                    Check(pages.StartsWith("Checks:") && !pages.Contains(":0 ") && !pages.Contains(":1 "), "track pages: " + pages);
                    Check(WcTrack.Pick((Last.Defaine.MenuCommandId)7), "other entries go to the game");
                    WcScreen.Show(false);
                    return true;
                }),
                (1f, () =>
                {
                    // the real field menu, if the field has built it: open it and read its entries
                    var all = Resources.FindObjectsOfTypeAll<Last.UI.KeyInput.MainMenuController>();
                    menu = all.Length > 0 ? all[0] : null;
                    if (menu == null) { Plugin.L.LogInfo("[wc test] no field menu built in this session: entry checked on a list only"); return true; }
                    menu.Show(false);
                    return true;
                }),
                (3f, () =>
                {
                    if (menu == null) return true;
                    var views = menu.commandMenuController?.contents;
                    var seen = new List<string>();
                    if (views != null)
                        for (int i = 0; i < views.Count; i++)
                            if (views[i]?.Data != null) seen.Add($"{(int)views[i].Data.Id}:{views[i].NameText?.text}");
                    Check(seen.Any(s => s == "100:Track"), $"field menu entries [{string.Join(", ", seen)}]");
                    try { menu.Close(); } catch (Exception e) { Plugin.L.LogInfo("[wc test] menu close: " + e.Message); }
                    return true;
                }),
            };
        }

        // the screen's fade overlay as drawn (FadeManager's image): black when it shows at nearly
        // full alpha. Its state flags (IsStateFadeOut) stay "out" after the Blackjack's boarding
        // arrival, whose fade-in is to a clear color, on a deck that shows fine in play
        internal static (bool, string) ScreenBlack()
        {
            try
            {
                var fm = Il2CppSystem.Fade.FadeManager.Instance;
                if (fm == null) return (false, "no fade manager");
                var img = fm.fadeImage;
                float imgA = img != null ? img.color.a : -1f;
                float crA = fm.canvasRenderer != null ? fm.canvasRenderer.GetAlpha() : -1f;
                bool shown = img != null && img.enabled && img.gameObject.activeInHierarchy;
                float alpha = Math.Min(imgA < 0 ? 1f : imgA, crA < 0 ? 1f : crA);
                bool black = shown && alpha > 0.9f;
                return (black, $"overlay {(shown ? "shown" : "hidden")}, image alpha {imgA:0.00}, renderer alpha {crA:0.00} (flags: out {fm.IsStateFadeOut()}, finished {fm.IsFadeFinish()})");
            }
            catch (Exception e) { return (false, "fade overlay unreadable: " + e.Message); }
        }

        // -nxppd (both presets): the battle result gives each standing character the whole EXP;
        // the PR's own result (BattleResultProvider.Genelate) built for the party, once with
        // everyone standing and once with one knocked out
        static bool NxppdCheck()
        {
            var party = UserDataManager.Instance()?.GetPartyMemberData();
            Check(party != null && party.Count >= 2, $"nxppd: a party of {party?.Count ?? 0}");
            if (party == null || party.Count < 2) return false;
            var flags = WcSeeds.Active?.Flags.Split(' ') ?? new string[0];
            int xpm = 1;
            int at = Array.IndexOf(flags, "-xpm");
            if (at >= 0 && at + 1 < flags.Length) int.TryParse(flags[at + 1], out xpm);
            bool on = flags.Contains("-nxppd");
            // the whole party standing, then one knocked out (the battle leaves them out of the result)
            foreach (int standing in new[] { party.Count, party.Count - 1 })
            {
                var targets = new Il2CppSystem.Collections.Generic.List<global::Last.Data.User.OwnedCharacterData>();
                for (int i = 0; i < standing; i++) targets.Add(party[i]);
                var result = new global::Last.Systems.BattleResultProvider().Genelate(
                    targets.Cast<Il2CppSystem.Collections.Generic.IEnumerable<global::Last.Data.User.OwnedCharacterData>>(), 120, 0,
                    new Il2CppSystem.Collections.Generic.List<global::Last.Data.DropItemData>(),
                    new global::Last.Battle.BattleSkillUpInformation(new Il2CppSystem.Collections.Generic.List<global::Last.Battle.BattleActLogData>(), 1, 0, 0));
                var gets = new List<int>();
                foreach (var c in result.CharacterList) gets.Add(c.GetExp);
                int each = on ? 120 * xpm : 120 * xpm / standing;
                Check(gets.Count == standing && gets.All(g => g == each),
                      $"nxppd ({(on ? "on" : "off")}, x{xpm}, {standing} standing): split between {result.DistributionExpCount}, " +
                      $"each {result.DistributionExp}, characters got [{string.Join(" ", gets)}], want {each} each ({WcRewards.LastSplit})");
            }
            return false;
        }

        // a Mug act by a unit, aimed at one unit (the mug_auto test's remembered acts)
        static BattleActData MugAt(BattleUnitData by, BattleUnitData at)
        {
            var mugs = new Il2CppSystem.Collections.Generic.List<Ability>();
            mugs.Add(MasterManager.Instance.GetData<Ability>(9));
            return new BattleActData(by, new BattleUnitData[] { at }, MasterManager.Instance.GetData<Command>(11), mugs,
                                     (BattleActData.ActType)0, (BattleActData.TargetType)(at == by ? 6 : 3), (BattleActData.ArmType)0,
                                     (BattleActData.ActAbilityType)0, 1, false);
        }

        // ---- WC's start state (the start-bits audit, 2026-11-21; tools/wc_events.py thamasa_burning_map,
        // thamasa_inn, mt_zozo_books, story_scenes' Doma room, phantom_forest_map, figaro_cave_siegfried;
        // WcEvents.GiveMorph): warp + flags, then the entities placed and the talk scripts' lines

        // a field entity of the map: "+" there and shown, "h" there but hidden, "-" not placed
        static string Shown(int id)
        {
            try
            {
                var e = Field()?.GetFieldEntity(id);
                if (e == null) return "-";
                return e.gameObject.activeInHierarchy ? "+" : "h";
            }
            catch (Exception) { return "?"; }
        }

        static string ShownAll(params int[] ids) => string.Join("", ids.Select(i => Shown(i)));
        static int Fl(DataStorage.Category c, int i) => WcEvents.Flag(c, i);
        static void SetFl(DataStorage.Category c, int i, int v) => DataStorage.Instance.Set(c, i, v);
        static bool Said(string key) => WcEvents.TestMessages.Any(m => m.StartsWith(key + ":"));
        static string Lines() => string.Join(" | ", WcEvents.TestMessages);
        static string OnMap() => $"map {UserDataManager.Instance()?.CurrentMapId}";

        // a script started with its menus answered by index (retried while the game refuses, as Run)
        static Func<bool> RunWith(string script, string what, params int[] answers)
        {
            int tries = 0;
            Func<bool> run = null;
            run = () =>
            {
                WcEvents.TestMessages.Clear();
                WcEvents.TestSelects.Clear();
                WcEvents.TestAnswers.Clear();
                WcEvents.TestAnswerKeys.Clear();
                if (WcEvents.RunScript(script, answers)) return true;
                WcEvents.TestAnswers.Clear();
                if (tries == 5) WcEvents.ResetMainCore();
                if (++tries < 15) { plan.Insert(0, (2f, run)); return true; }
                Check(false, $"{what}: {script} didn't start in 30 s");
                return false;
            };
            return run;
        }

        // 1. WoB Thamasa (map 180) as WC's start leaves it: Leo's grave, his sword and the three townsfolk
        // (ev_e_0552_2: grave 172, sword 173, man 174, boy 175, woman 176), not the path's blocker
        // (ev_e_0552_1: 58) nor the townsfolk vanilla places after Zozo (ev_e_0413: 107 108 152 110);
        // ScenarioFlag1 203 and ScenarioFlag2 26 stay clear. While the burning house is open: its crowd
        // (ev_e_0523: Strago 95, the boy 149) and the grave layer's man and boy hidden
        static List<(float, Func<bool>)> ThamasaTownPlan()
        {
            const int Thamasa = 180, Inn = 184, Fire = 35;
            int keep = 0;
            return new List<(float, Func<bool>)>
            {
                (0f, () =>
                {
                    keep = Fl(DataStorage.Category.kScenarioFlag4, Fire);
                    SetFl(DataStorage.Category.kScenarioFlag4, Fire, 1);
                    Check(WcEvents.GotoMap(Thamasa, 1), "thamasa_town: warp to Thamasa (map 180), the burning house done");
                    return true;
                }),
                (8f, () =>
                {
                    Check(ShownAll(172, 173, 174, 175, 176) == "+++++",
                          $"thamasa_town: Leo's grave, sword and three townsfolk (172-176) {ShownAll(172, 173, 174, 175, 176)} ({OnMap()})");
                    Check(ShownAll(58, 107, 108, 152, 110, 95) == "------",
                          $"thamasa_town: no blocker (58), no townsfolk after Zozo (107 108 152 110), no fire crowd (95): {ShownAll(58, 107, 108, 152, 110, 95)}");
                    int f203 = Fl(DataStorage.Category.kScenarioFlag1, 203), f26 = Fl(DataStorage.Category.kScenarioFlag2, 26);
                    Check(f203 == 0 && f26 == 0, $"thamasa_town: ScenarioFlag1 203 = {f203}, ScenarioFlag2 26 = {f26} (both stay clear)");
                    SetFl(DataStorage.Category.kScenarioFlag4, Fire, 0);
                    Check(WcEvents.GotoMap(Inn, 1), "thamasa_town: out to the inn (map 184)");
                    return true;
                }),
                (6f, () => { Check(WcEvents.GotoMap(Thamasa, 1), "thamasa_town: back to Thamasa, the burning house open"); return true; }),
                (8f, () =>
                {
                    Check(ShownAll(95, 149) == "++", $"thamasa_town: the fire's crowd (Strago 95, the boy 149) {ShownAll(95, 149)} ({OnMap()})");
                    Check(ShownAll(172, 173, 176) == "+++" && Shown(174) != "+" && Shown(175) != "+",
                          $"thamasa_town: the grave, sword and woman {ShownAll(172, 173, 176)}; the grave layer's man and boy hidden: {ShownAll(174, 175)}");
                    SetFl(DataStorage.Category.kScenarioFlag4, Fire, keep);
                    return true;
                }),
            };
        }

        // 2. Thamasa's inn: the after-fire innkeeper (map 184, ev_e_0540: entity 5; not the stranger's 1
        // nor the fire night's 11 / 8), 1 gil a night; with -cg and Strago (8) not recruited, WC's stranger
        // price (WC_THAMASA_INN, then "...You don't have enough money."), nothing paid. The WoR's (map 309) too
        static List<(float, Func<bool>)> ThamasaInnPlan()
        {
            const int WobInn = 184, WorInn = 309, Strago = 8;
            bool cg = WcSeeds.Active?.Flags.Split(' ').Contains("-cg") == true;
            int keep = 0, gil = 0;
            int Gil() => UserDataManager.Instance()?.OwendGil ?? -1;
            Func<bool> Ask(string script, bool recruited) => () =>
            {
                gil = Gil();
                SetFl(DataStorage.Category.kScenarioFlag4, WcEvents.Recruited + Strago, recruited ? 1 : 0);
                return RunWith(script, "thamasa_inn", 0)();
            };
            Func<bool> Paid(string where) => () =>
            {
                Check(gil - Gil() == 1 && Said("S9994_999_a_07") && !Said("WC_THAMASA_INN"),
                      $"thamasa_inn ({where}): a night for 1 gil: {gil} -> {Gil()}; lines {Lines()}");
                return true;
            };
            Func<bool> Refused(string where) => () =>
            {
                Check(Said("WC_THAMASA_INN") && Said("S9999_999_a_12") && Gil() == gil,
                      $"thamasa_inn ({where}, -cg, Strago not recruited): the stranger's price, refused: gil {gil} -> {Gil()}; lines {Lines()}");
                return true;
            };
            var steps = new List<(float, Func<bool>)>
            {
                (0f, () =>
                {
                    keep = Fl(DataStorage.Category.kScenarioFlag4, WcEvents.Recruited + Strago);
                    if (Gil() < 10) UserDataManager.Instance().OwendGil = 1000;
                    Check(WcEvents.GotoMap(WobInn, 1), "thamasa_inn: warp to Thamasa's inn (map 184)");
                    return true;
                }),
                (8f, () =>
                {
                    Check(ShownAll(5, 1, 11, 8) == "+---", $"thamasa_inn: the after-fire innkeeper (5), not the stranger's (1) or the fire's (11, 8): {ShownAll(5, 1, 11, 8)} ({OnMap()})");
                    return true;
                }),
                (0f, Ask("sc_sty_20271_4", true)),
                (12f, Paid("WoB")),
            };
            if (cg) steps.AddRange(new List<(float, Func<bool>)> { (2f, Ask("sc_sty_20271_4", false)), (6f, Refused("WoB")) });
            steps.Add((0f, () => { Check(WcEvents.GotoMap(WorInn, 1), "thamasa_inn: warp to the WoR's inn (map 309)"); return true; }));
            steps.Add((8f, Ask("sc_sty_20461_4", true)));
            steps.Add((12f, Paid("WoR")));
            if (cg) steps.AddRange(new List<(float, Func<bool>)> { (2f, Ask("sc_sty_20461_4", false)), (6f, Refused("WoR")) });
            steps.Add((0f, () => { SetFl(DataStorage.Category.kScenarioFlag4, WcEvents.Recruited + Strago, keep); return true; }));
            return steps;
        }

        // 3. Mt. Zozo after its check (ScenarioFlag2 109): no key on the cliff (map 475, ev_e_0736: entities
        // 1, 2); Cyan's chest (map 474, sc_e_0737) locked, also with the key taken (110): "It's locked."
        // only, no "Books" (ScenarioFlag2 111 stays clear)
        static List<(float, Func<bool>)> MtZozoBooksPlan()
        {
            const int Cliff = 475, Room = 474;
            int k109 = 0, k110 = 0, k111 = 0;
            return new List<(float, Func<bool>)>
            {
                (0f, () =>
                {
                    k109 = Fl(DataStorage.Category.kScenarioFlag2, 109);
                    k110 = Fl(DataStorage.Category.kScenarioFlag2, 110);
                    k111 = Fl(DataStorage.Category.kScenarioFlag2, 111);
                    SetFl(DataStorage.Category.kScenarioFlag2, 109, 1);
                    SetFl(DataStorage.Category.kScenarioFlag2, 110, 0);
                    SetFl(DataStorage.Category.kScenarioFlag2, 111, 0);
                    Check(WcEvents.GotoMap(Cliff, 1), "mt_zozo_books: warp to Mt. Zozo's cliff (map 475), the check done");
                    return true;
                }),
                (8f, () =>
                {
                    Check(ShownAll(1, 2) == "--", $"mt_zozo_books: no sparkling key (ev_e_0736: 1, 2): {ShownAll(1, 2)} ({OnMap()})");
                    SetFl(DataStorage.Category.kScenarioFlag2, 110, 1);
                    Check(WcEvents.GotoMap(Room, 1), "mt_zozo_books: warp to Cyan's room (map 474), as a save that took the key");
                    return true;
                }),
                (8f, RunWith("sc_e_0737", "mt_zozo_books")),
                (4f, () =>
                {
                    int f111 = Fl(DataStorage.Category.kScenarioFlag2, 111);
                    Check(Said("E0737_00_474_a_01") && !Said("E0737_00_474_a_02") && f111 == 0,
                          $"mt_zozo_books: Cyan's chest stays locked (ScenarioFlag2 111 = {f111}); lines {Lines()}");
                    SetFl(DataStorage.Category.kScenarioFlag2, 109, k109);
                    SetFl(DataStorage.Category.kScenarioFlag2, 110, k110);
                    SetFl(DataStorage.Category.kScenarioFlag2, 111, k111);
                    return true;
                }),
            };
        }

        // 4. Doma Castle, Cyan's family's room (map 81): Mina and Shun (ev_e_0120: entities 1, 3) not lying there
        static List<(float, Func<bool>)> DomaFamilyPlan()
        {
            const int Room = 81;
            int keep = 0;
            return new List<(float, Func<bool>)>
            {
                (0f, () =>
                {
                    keep = Fl(DataStorage.Category.kScenarioFlag2, 3);
                    SetFl(DataStorage.Category.kScenarioFlag2, 3, 0);
                    Check(WcEvents.GotoMap(Room, 1), "doma_family: warp to Cyan's family's room (map 81)");
                    return true;
                }),
                (8f, () =>
                {
                    Check(ShownAll(1, 3) == "--", $"doma_family: Mina (1) and Shun (3) not there: {ShownAll(1, 3)} ({OnMap()})");
                    SetFl(DataStorage.Category.kScenarioFlag2, 3, keep);
                    return true;
                }),
            };
        }

        // 5. The Phantom Forest's ghost merchant (map 352, in the room's entity_default: entity 30 with the
        // train's shop script, 1254) shown after the Phantom Train check (ScenarioFlag4 55), hidden before;
        // talking gives the train ghost's offer (E1049_00_360_a_01, (Shop.) / (Don't shop.))
        static List<(float, Func<bool>)> GhostShopPlan()
        {
            const int Forest = 352, Forest1 = 351, Train = 55, Ghost = 30, ShopScript = 1254;
            int keep = 0;
            int ScriptOf(int id)
            {
                try { return Field()?.GetFieldEntity(id)?.Property?.TryCast<Last.Map.PropertyEvent>()?.ScriptId ?? -1; }
                catch (Exception) { return -1; }
            }
            return new List<(float, Func<bool>)>
            {
                (0f, () =>
                {
                    keep = Fl(DataStorage.Category.kScenarioFlag4, Train);
                    SetFl(DataStorage.Category.kScenarioFlag4, Train, 1);
                    Check(WcEvents.GotoMap(Forest, 1), "ghost_shop: warp to the Phantom Forest's spring room (map 352), the train done");
                    return true;
                }),
                (8f, () =>
                {
                    Check(Shown(Ghost) == "+" && ScriptOf(Ghost) == ShopScript,
                          $"ghost_shop: the ghost merchant (entity {Ghost}) {Shown(Ghost)}, script {ScriptOf(Ghost)} (want {ShopScript}) ({OnMap()})");
                    return true;
                }),
                (0f, RunAnswering("sc_e_1112", "N000_C07_360_c_02", "ghost_shop")),
                (4f, () =>
                {
                    Check(Said("E1049_00_360_a_01") && WcEvents.TestSelects.Any(s => s.Contains("N000_C07_360_c_01")),
                          $"ghost_shop: the train ghost's offer: lines {Lines()}; menus [{string.Join(" ", WcEvents.TestSelects)}]");
                    SetFl(DataStorage.Category.kScenarioFlag4, Train, 0);
                    Check(WcEvents.GotoMap(Forest1, 1), "ghost_shop: out to the forest's first map (351)");
                    return true;
                }),
                (6f, () => { Check(WcEvents.GotoMap(Forest, 1), "ghost_shop: back to the spring room, the train not done"); return true; }),
                (8f, () =>
                {
                    Check(Shown(Ghost) != "+", $"ghost_shop: no merchant before the train (hidden): {Shown(Ghost)}");
                    SetFl(DataStorage.Category.kScenarioFlag4, Train, keep);
                    return true;
                }),
            };
        }

        // 6. The WoR Figaro cave's entrance (map 452): with -cg and Edgar (5) not recruited Siegfried
        // (ev_e_0666: entity 6) stands in the passage and says WC's line only (WC_SIEGFRIED_WAIT): he stays,
        // ScenarioFlag2 75 stays set. Edgar recruited (or no -cg): not there
        static List<(float, Func<bool>)> SiegfriedGatePlan()
        {
            const int Cave = 452, Below = 451, Edgar = 5, Siegfried = 6;
            bool cg = WcSeeds.Active?.Flags.Split(' ').Contains("-cg") == true;
            int keep = 0;
            var steps = new List<(float, Func<bool>)>
            {
                (0f, () =>
                {
                    keep = Fl(DataStorage.Category.kScenarioFlag4, WcEvents.Recruited + Edgar);
                    SetFl(DataStorage.Category.kScenarioFlag4, WcEvents.Recruited + Edgar, 0);
                    Check(WcEvents.GotoMap(Cave, 1), $"siegfried_gate: warp to the WoR Figaro cave's entrance (map 452), Edgar not recruited, -cg {(cg ? "on" : "off")}");
                    return true;
                }),
                (8f, () =>
                {
                    Check(Shown(Siegfried) == (cg ? "+" : "-"), $"siegfried_gate: Siegfried (6) {Shown(Siegfried)} (want {(cg ? "there" : "not there")}) ({OnMap()})");
                    return true;
                }),
            };
            if (cg)
                steps.AddRange(new List<(float, Func<bool>)>
                {
                    (0f, RunWith("sc_e_0666", "siegfried_gate")),
                    (5f, () =>
                    {
                        int f75 = Fl(DataStorage.Category.kScenarioFlag2, 75);
                        Check(Said("WC_SIEGFRIED_WAIT") && !Said("E0666_00_448_a_01") && f75 == 1 && Shown(Siegfried) == "+",
                              $"siegfried_gate: WC's line, he stays ({Shown(Siegfried)}), ScenarioFlag2 75 = {f75}; lines {Lines()}");
                        SetFl(DataStorage.Category.kScenarioFlag4, WcEvents.Recruited + Edgar, 1);
                        Check(WcEvents.GotoMap(Below, 1), "siegfried_gate: on into the cave (map 451)");
                        return true;
                    }),
                    (6f, () => { Check(WcEvents.GotoMap(Cave, 1), "siegfried_gate: back to the entrance, Edgar recruited"); return true; }),
                    (8f, () => { Check(Shown(Siegfried) == "-", $"siegfried_gate: Siegfried gone once Edgar is recruited: {Shown(Siegfried)}"); return true; }),
                });
            steps.Add((0f, () => { SetFl(DataStorage.Category.kScenarioFlag4, WcEvents.Recruited + Edgar, keep); return true; }));
            return steps;
        }

        // 7. Morph from the start (WcEvents.GiveMorph): whoever the seed dealt Morph (the PR's command 9 in
        // the character's row; else Terra, given it for the test) has the Trance command (8), listed, and
        // usable as the game's own rule has it: morph points over 7 (CommandAvailableData.IsAvailable)
        static List<(float, Func<bool>)> MorphStartPlan()
        {
            int holder = -1;
            bool forced = false;
            bool Has(OwnedCharacterData c, int id) => WcEspers.HasCommand(c, id);
            return new List<(float, Func<bool>)>
            {
                (2f, () =>
                {
                    for (int id = 1; id <= 14 && holder < 0; id++)
                    {
                        var row = MasterManager.Instance.GetData<CharacterStatus>(id);
                        if (row != null && new[] { row.CommandId1, row.CommandId2, row.CommandId3, row.CommandId4, row.CommandId5, row.CommandId6 }
                                .Contains(WcEvents.MorphSlot)) holder = id;
                    }
                    if (holder < 0) { holder = 1; forced = true; }
                    if (OwnedChar(holder) == null || !WcEvents.IsRecruited(holder)) WcEvents.Join(holder);
                    var c = OwnedChar(holder);
                    if (forced && c?.CommandList != null && !Has(c, WcEvents.MorphSlot))
                        c.CommandList.Add(MasterManager.Instance.GetData<Command>(WcEvents.MorphSlot));
                    Check(c != null && Has(c, WcEvents.MorphSlot),
                          $"morph_start: character {holder} holds the Morph slot (command 9){(forced ? " (given for the test: no one was dealt Morph)" : " (the seed's)")}");
                    return c != null;
                }),
                (8f, () =>
                {
                    var c = OwnedChar(holder);
                    var udm = UserDataManager.Instance();
                    var cmd = MasterManager.Instance.GetData<Command>(WcEvents.TranceCommand);
                    var cad = new Serial.FF6.Management.CommandAvailableData();
                    bool Usable() => cad.IsAvailable(c, cmd,
                        udm.GetPartyMemberData().Cast<Il2CppSystem.Collections.Generic.IEnumerable<OwnedCharacterData>>(),
                        udm.GetOwnedItemsClone().Cast<Il2CppSystem.Collections.Generic.IEnumerable<OwnedItemData>>());
                    bool has = Has(c, WcEvents.TranceCommand), listed = has && cad.IsCommandRelease(c, cmd);
                    int keep = c.MagicLearningValue;
                    c.MagicLearningValue = 0;
                    bool none = has && Usable();
                    c.MagicLearningValue = 8;
                    bool some = has && Usable();
                    c.MagicLearningValue = keep;
                    Check(has && listed && !none && some,
                          $"morph_start: character {holder} has Trance (8) {has}, listed {listed}, usable with 0 morph points {none}, with 8 {some} " +
                          $"(given: {WcEvents.LastMorphGiven}; commands [{string.Join(" ", c.CommandList.ToArray().Select(x => x?.Id ?? -1))}])");
                    if (forced)
                    {
                        foreach (var id in new[] { WcEvents.TranceCommand, WcEvents.MorphSlot })
                            foreach (var x in c.CommandList.ToArray()) if (x?.Id == id) { c.CommandList.Remove(x); break; }
                    }
                    return true;
                }),
            };
        }

        // 8. The Phantom Forest's spring room on a first visit (ScenarioFlag1 106 clear, map 352): no walk to the
        // spring (ev_e_0158's trigger, entity 9, not placed), the spring itself there (sc_e_0159: entities 1-4)
        static List<(float, Func<bool>)> ForestSpringPlan()
        {
            const int Forest = 352;
            int keep = 0;
            return new List<(float, Func<bool>)>
            {
                (0f, () =>
                {
                    keep = Fl(DataStorage.Category.kScenarioFlag1, 106);
                    SetFl(DataStorage.Category.kScenarioFlag1, 106, 0);
                    WcEvents.TestMessages.Clear();
                    Check(WcEvents.GotoMap(Forest, 1), "forest_spring: warp to the Phantom Forest's spring room (map 352), first visit");
                    return true;
                }),
                (8f, () =>
                {
                    int f106 = Fl(DataStorage.Category.kScenarioFlag1, 106);
                    Check(Shown(9) == "-" && ShownAll(1, 2, 3, 4) == "++++" && f106 == 0 && !Said("S9999_999_a_03"),
                          $"forest_spring: no walk to the spring (trigger 9 {Shown(9)}, ScenarioFlag1 106 = {f106}), the spring there {ShownAll(1, 2, 3, 4)}; lines {Lines()} ({OnMap()})");
                    SetFl(DataStorage.Category.kScenarioFlag1, 106, keep);
                    return true;
                }),
            };
        }

        static List<(float, Func<bool>)> CheckPlans(string name)
        {
            if (name == "airship_crew") return AirshipCrewPlan();
            if (name == "eu_unequip") return EuUnequipPlan();
            if (name == "eu_equip") return Plan_eu_equip(name);
            if (name == "reward_wallet") return Plan_reward_wallet(name);
            if (name == "reward_mp") return Plan_reward_mp(name);
            if (name == "thamasa_town") return ThamasaTownPlan();
            if (name == "thamasa_inn") return ThamasaInnPlan();
            if (name == "mt_zozo_books") return MtZozoBooksPlan();
            if (name == "doma_family") return DomaFamilyPlan();
            if (name == "ghost_shop") return GhostShopPlan();
            if (name == "siegfried_gate") return SiegfriedGatePlan();
            if (name == "morph_start") return MorphStartPlan();
            if (name == "forest_spring") return ForestSpringPlan();
            if (name == "empty_chest") return Plan_empty_chest(name);
            if (name == "train_chest") return Plan_train_chest(name);
            if (name == "monster_box") return Plan_monster_box(name);
            if (name == "veldt") return Plan_veldt(name);
            if (name == "veldt_reward") return Plan_veldt_reward(name);
            if (name == "doma_play") return Plan_doma_play(name);
if (name == "doma_start") return Plan_doma_start(name);
            if (name == "vargas") return Plan_vargas(name);
            if (name == "fc_trip") return Plan_fc_trip(name);
            if (name == "encounters") return Plan_encounters(name);
            if (name == "atb") return Plan_atb(name);
            if (name == "snes_rules") return Plan_snes_rules(name);
            if (name == "status_ticks")
                return StatusTicksPlan();
            if (name == "turn_cycle")
                return TurnCyclePlan();
            if (name == "status_timers")
                return StatusTimersPlan();
if (name == "enemy_ai") return Plan_enemy_ai(name);
            if (name == "entry_exit_probe") return Plan_entry_exit_probe(name);
            if (name == "enemy_ai_sweep" || name.StartsWith("enemy_ai_sweep@")) return Plan_enemy_ai_sweep(name);
            if (name.StartsWith("enemy_ai_each@")) return Plan_enemy_ai_each(name);
            if (name.StartsWith("enemy_ai@")) return Plan_enemy_ai_2(name);
            if (name == "ability_scaling") return Plan_ability_scaling(name);
            if (name == "mug_auto") return Plan_mug_auto(name);
            if (name == "nxppd")
                return new List<(float, Func<bool>)> { (0f, NxppdCheck) };
            if (name == "getitem") return Plan_getitem(name);
            if (name == "kt_skip") return Plan_kt_skip(name);
            if (name == "fade_selftest") return Plan_fade_selftest(name);
            var seed = WcSeeds.Active;
            var (module, rn) = SubChecks.TryGetValue(name, out var sub) ? sub : (name, 0);
            if (!seed.Checks.TryGetValue(module, out var c)) { Check(false, $"the seed has no check {module}"); return null; }
            if (req.TryGetValue("reward", out var forced))   // test only: "character 9", "esper 76", "item 12"
            {
                var parts = forced.Split(' ');
                c.Rewards[0] = (parts[0], int.Parse(parts[1]));
                Plugin.L.LogInfo($"[wc test] {name}'s reward set to {forced} for this test");
            }
            int flag = CheckFlags.TryGetValue(name == "search_skies" ? module : name, out var f) ? f : -1;
            int F4(int i) => WcEvents.Flag(DataStorage.Category.kScenarioFlag4, i);
            int before = 0, picks = 0;
            Func<bool> Warp(int map, int point) => () => { Check(WcEvents.GotoMap(map, point), $"warp to map {map}"); return true; };
            int tries = 0;
            Func<bool> Run(string script, params int[] answers)
            {
                Func<bool> run = null;
                run = () =>
                {
                    WcEvents.TestMessages.Clear();
                    before = RewardCount(c, rn);
                    picks = WcEvents.PartyRefreshes;
                    WcEvents.TestAnswers.Clear();
                    if (WcEvents.RunScript(script, answers)) { tries = 0; return true; }
                    if (tries == 5) WcEvents.ResetMainCore();
                    if (++tries < 15) { plan.Insert(0, (2f, run)); return true; }   // the game refuses for a while after a script
                    Check(false, $"{script} didn't start in 30 s");
                    return false;
                };
                return run;
            }
            int polls = 0;
            Func<bool> Expect(bool rewarded)
            {
                Func<bool> expect = null;
                expect = () =>
                {
                    // the scene may still be running (fades, sounds): poll up to 20 s
                    if (rewarded && F4(flag) != 1 && ++polls < 20) { plan.Insert(0, (1f, expect)); return true; }
                    polls = 0;
                    return ExpectNow(rewarded);
                };
                return expect;
            }
            bool ExpectNow(bool rewarded)
            {
                int now = RewardCount(c, rn);
                int gained = FlagOnly.Contains(name) ? 0 : 1;
                if (rewarded && WcEvents.TestStoryShownWhy.Count > 0)
                    Plugin.L.LogInfo($"[wc test] {name}: story lines play would show: {string.Join(" | ", WcEvents.TestStoryShownWhy)}");
                WcEvents.TestStoryShownWhy.Clear();
                // WC's party pick after a character reward (the screen stood in for: the recruit
                // put in front), the leader redrawn where the party stands, the screen not black
                if (rewarded && c.Rewards.Count > rn && c.Rewards[rn].Kind == "character")
                {
                    // (read at the pick: a scene may switch parties after it, Phoenix Cave's merge)
                    int leader = WcEvents.PickLeader;
                    Check(WcEvents.PartyRefreshes == picks + 1 && leader == c.Rewards[rn].Id && WcEvents.PickScreenBlack == WcEvents.RewardScreenBlack,
                          $"{name}: party pick after recruiting {c.Rewards[rn].Id}: {WcEvents.PartyRefreshes - picks} refresh, leader {leader} at the pick, " +
                          $"screen {(WcEvents.PickScreenBlack ? "black" : "shown")} after it ({(WcEvents.RewardScreenBlack ? "black" : "shown")} at the reward)");
                }
                Check(rewarded ? now == before + gained && F4(flag) == 1 : now == before && F4(flag) == 0,
                      $"{name}: {(rewarded ? "rewarded" : "nothing yet")} (have {before} -> {now}, check flag {F4(flag)}; " +
                      $"reward {string.Join(" ", c.Rewards.Select(r => r.Kind + " " + r.Id))}, gate {c.Gate}, recruited {(c.Gate > 0 && WcEvents.IsRecruited(c.Gate))}; " +
                      $"{WcEvents.LastReward}; messages: {string.Join(" | ", WcEvents.TestMessages)})");
                return true;
            }
            Func<bool> GateIs(bool open) => () =>
            {
                WcEvents.Gate(module);
                Check(F4(WcEvents.Scratch) == (open ? 1 : 0), $"{name} gate {(open ? "open" : "closed")} (gate character {c.Gate}, recruited {WcEvents.IsRecruited(c.Gate)})");
                return true;
            };
            Func<bool> Recruit(int id) => () => { DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, WcEvents.Recruited + id, 1); return true; };
            // after the reward's scene the screen is faded in again (the user's playtest,
            // 2026-11-06: the Imperial Camp's reward left it black on the world map)
            int blackPolls = 0;
            Func<bool> NotBlack()
            {
                Func<bool> check = null;
                check = () =>
                {
                    var (black, state) = ScreenBlack();
                    Plugin.L.LogInfo($"[wc test]   {name}: {state}; map {UserDataManager.Instance()?.CurrentMapId}, script '{Integrator.Instance?.scriptName}'");
                    if (black && ++blackPolls < 10) { plan.Insert(0, (1f, check)); return true; }   // a transition may still run
                    blackPolls = 0;
                    Check(!black, $"{name}: the screen faded back in after the reward (map {UserDataManager.Instance()?.CurrentMapId})");
                    return true;
                };
                return check;
            }
            if (name == "search_skies")
            {
                // WC event/airship.py: the Falcon's wheel in the WoR offers (Search The Skies) while
                // Doom Gaze's gate is met (Setzer, with -cg) and he isn't beaten (tools/wc_events.py
                // falcon_helm). Gate closed: not offered; the check done: not offered; open: offered and
                // picked: the Falcon takes off (ScenarioFlag4 128 pending) and its arrival in the sky
                // starts the PR's Doom Gaze fight (party 464, the seed's boss swapped in), ended here by
                // force; then the sky fight's own scripts: his reward scene once he falls (sc_e_0964_2:
                // the seed's reward, the check flag), or he flies off with his HP kept (no reward, the
                // choice stays). Each wheel run in a visit of its own (deck 268, from the cabin 269)
                const int Deck = 268, Cabin = 269, DoomGazeParty = 464, SkiesPending = 128;
                const string Skies = "WC_HELM_SKIES", NeverMind = "E0412_00_119_c_02", Wheel = "sc_e_FAoutboard";
                var steps = new List<(float, Func<bool>)>
                {
                    (0f, () =>
                    {
                        // in the WoR with the Falcon, as the Blackjack's wheel leaves it (worlds=2)
                        WcEvents.MoveAirship(4, 5, 2);
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 97, 1);
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 41, 1);
                        return true;
                    }),
                };
                void Visit() { steps.Add((0f, Warp(Cabin, 1))); steps.Add((5f, Warp(Deck, 1))); }
                Func<bool> Offered(bool want, string when) => () =>
                {
                    var menu = WcEvents.TestSelects.FirstOrDefault() ?? "";
                    Check(menu != "" && menu.Split('/').Contains(Skies) == want,
                          $"search_skies ({when}): the wheel {(want ? "offers" : "doesn't offer")} (Search the skies.): [{menu}] " +
                          $"(gate {c.Gate}, recruited {(c.Gate > 0 && WcEvents.IsRecruited(c.Gate))}, check flag {F4(flag)})");
                    return true;
                };
                Func<bool> SetDone(int v) => () => { DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, flag, v); return true; };
                if (c.Gate > 0 && !WcEvents.IsRecruited(c.Gate))
                {
                    Visit();
                    steps.Add((0f, GateIs(false)));
                    steps.Add((6f, RunAnswering(Wheel, NeverMind, name)));
                    steps.Add((3f, Offered(false, "gate closed")));
                    steps.Add((0f, Recruit(c.Gate)));
                }
                Visit();
                steps.Add((0f, GateIs(true)));
                steps.Add((0f, SetDone(1)));
                steps.Add((6f, RunAnswering(Wheel, NeverMind, name)));
                steps.Add((3f, Offered(false, "Doom Gaze's check done")));
                steps.Add((0f, SetDone(0)));
                Visit();
                steps.Add((0f, () =>
                {
                    before = RewardCount(c, rn);
                    picks = WcEvents.PartyRefreshes;
                    WcEvents.TestStoryShownWhy.Clear();
                    return true;
                }));
                // the pick ends the wheel's script with the deck's fight: the plan goes on
                steps.Add((6f, RunAnswering(Wheel, Skies, name, hold: false)));
                steps.Add((1f, Offered(true, "gate open, Doom Gaze not beaten")));
                int fightPolls = 0;
                Func<bool> fight = null;
                fight = () =>
                {
                    var b = BattlePlugManager.Instance();
                    bool on = b != null && b.IsBattle();
                    if (!on && ++fightPolls < 30) { plan.Insert(0, (2f, fight)); return true; }
                    Check(on, $"search_skies: a battle after the take-off within {fightPolls * 2} s (map {UserDataManager.Instance()?.CurrentMapId}, " +
                              $"pending flag {F4(SkiesPending)}, script '{Integrator.Instance?.scriptName}')");
                    return on;
                };
                steps.Add((2f, fight));
                steps.Add((3f, () =>
                {
                    var b = BattlePlugManager.Instance();
                    var ids = b?.GetEnemyUnits()?.Select(e => e?.GetMonster()?.Id ?? 0).Where(i => i > 0).ToList() ?? new List<int>();
                    Check(WcBattles.CurrentOriginal == DoomGazeParty,
                          $"search_skies: Doom Gaze's fight: the sky's party {WcBattles.CurrentOriginal} (want {DoomGazeParty}; the seed's {WcBattles.Map(DoomGazeParty)}), " +
                          $"battle party {b?.MonsterParty?.Id}, monsters [{string.Join(" ", ids)}]");
                    // on the deck since 0.6.11 (the sky's start lost the game): the pending flag stays on until
                    // the fight's follow-up (the wheel's own script) clears it
                    Check(F4(SkiesPending) == 1 && UserDataManager.Instance()?.CurrentMapId != WcEvents.WorMap,
                          $"search_skies: fought on the Falcon's deck (map {UserDataManager.Instance()?.CurrentMapId}), the pending flag on ({F4(SkiesPending)})");
                    // over by force (a won fight's result screen waits for a button headless)
                    var j = b?.BattleEndJugment;
                    if (j != null)
                    {
                        j.SetEnabled(true);
                        j.CurrentEndJugmentType = BattleEndJugment.EndJugmentType.Normal;
                        j.isForce = true;
                    }
                    return true;
                }));
                int quiet = 0, looks = 0;
                Func<bool> settle = null;
                settle = () =>
                {
                    var b = BattlePlugManager.Instance();
                    bool idle = (b == null || !b.IsBattle()) && Object.FindObjectOfType<FieldMap>() != null && !WcEvents.EventBusy();
                    quiet = idle ? quiet + 1 : 0;
                    if (quiet < 3 && ++looks < 40) { plan.Insert(0, (2f, settle)); return true; }
                    Plugin.L.LogInfo($"[wc test] search_skies: after the fight: map {UserDataManager.Instance()?.CurrentMapId}, check flag {F4(flag)}, " +
                                     $"Doom Gaze's kept HP (ScenarioVariable 3) {WcEvents.Flag(DataStorage.Category.kScenarioVariable, 3)}, quiet {quiet} after {looks} looks");
                    Check(quiet >= 3, $"search_skies: the field is back after the fight (map {UserDataManager.Instance()?.CurrentMapId})");
                    Check(F4(SkiesPending) == 0, $"search_skies: the follow-up cleared the pending flag ({F4(SkiesPending)})");
                    if (F4(flag) == 1) return ExpectNow(true);   // he fell: the reward scene gave the seed's reward
                    Check(RewardCount(c, rn) == before && F4(flag) == 0,
                          $"search_skies: Doom Gaze flew off (the forced end): no reward, the check (and the wheel's choice) stays open ({WcEvents.LastReward})");
                    return true;
                };
                steps.Add((3f, settle));
                steps.Add((0f, NotBlack()));
                return steps;
            }
            if (name == "sealed_gate")
            {
                // the gate's map (411) and its whole scene chain (sc_e_0460 -> map 606 -> sc_e_0460_3):
                // a bare warp into 606 never finishes loading without its scene
                Func<bool> recruit = () => { if (c.Gate > 0 && !WcEvents.IsRecruited(c.Gate)) Recruit(c.Gate)(); return true; };
                return new List<(float, Func<bool>)> { (0, Warp(411, 1)), (0, recruit), (0, GateIs(true)), (6, Run("sc_e_0460")), (30, Expect(true)) };
            }
            if (name == "final_kefka")
            {
                // the stairway to Kefka (map 570) with the seed's "Unlock Final Kefka" objective not met:
                // the party stays (no map change), WC_REWARD says why
                var unlock = seed.Objectives.FirstOrDefault(o => o.ResultName == "Unlock Final Kefka");
                Func<bool> sealedCheck = () =>
                {
                    bool expectOpen = unlock == null || unlock.Conditions.Count(k => WcTrack.Met(k) != false) >= unlock.Required;
                    var map = UserDataManager.Instance().CurrentMapId;
                    Check(expectOpen ? map == 571 : map == 570 && F4(WcEvents.Scratch) == 0,
                          $"final Kefka {(expectOpen ? "open" : "sealed")}: map {map}, scratch {F4(WcEvents.Scratch)}; {MessageManager.Instance?.GetMessage("WC_REWARD", false)}");
                    return true;
                };
                return new List<(float, Func<bool>)> { (0, Warp(570, 1)), (6, Run("sc_e_1002")), (6, sealedCheck) };
            }
            if (name == "auction_1" || name == "auction_2")
            {
                // Jidoor's auction (map 111): the sale is random (1 in 5 in the WoB), so open the
                // auction again (ScenarioFlag1 203 open, map-local 1 = one sale per visit), with
                // gil to win the bidding, until our sale comes up
                int auctionTries = 0;
                Func<bool> again = null;
                again = () =>
                {
                    if (auctionTries > 0 && F4(flag) == 1) return ExpectNow(true);
                    if (++auctionTries > 40) { Check(false, $"{name}: the sale didn't come up in 40 auctions"); return true; }
                    DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag1, 203, 1);
                    DataStorage.Instance.Set(DataStorage.Category.kMapLocalVariable, 1, 0);
                    var udm = UserDataManager.Instance();
                    if (udm.OwendGil < 30000) new OwnedItemClient().AddGil(30000 - udm.OwendGil);
                    plan.Insert(0, (2f, again));
                    plan.Insert(0, (0f, Run("sc_e_0422")));
                    return true;
                };
                return new List<(float, Func<bool>)> { (0, Warp(111, 1)), (2, again) };
            }
            if (Dragons.TryGetValue(name, out var dragonFlag))
            {
                // a dragon "defeated" (its PR flag), then the countdown (Narshe's copy, map 19)
                Func<bool> slain = () => { DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, dragonFlag, 1); return true; };
                return new List<(float, Func<bool>)> { (0, Warp(19, 1)), (0, slain), (6, Run("sc_e_0975")), (2, Expect(true)) };
            }
            if (name == "imperial_camp")
            {
                // the gate met before going in; the trigger down the camp's middle (36, 23) takes the
                // party to Cyan's duels (WC's "Cyan rushes in": (8, 25); the user's playtest,
                // 2026-11-06), run as stepping on it does; then the reward. PlayerCell reads the
                // map's (36, 3) entrance as y 2: one row off the map's own coordinates
                Func<bool> recruit = () => { if (c.Gate > 0 && !WcEvents.IsRecruited(c.Gate)) Recruit(c.Gate)(); return true; };
                string start = "?";
                Func<bool> atEntry = () => { var e0 = WcEvents.PlayerCell(); start = e0.HasValue ? $"{e0.Value.x},{e0.Value.y}" : "?"; return true; };
                Func<bool> atCyan = () =>
                {
                    var cell = WcEvents.PlayerCell();
                    Check(cell.HasValue && cell.Value.x == 8 && (cell.Value.y == 24 || cell.Value.y == 25),
                          $"imperial_camp: the walk's trigger took the party from {start} to Cyan's duels (8, 25): at {(cell.HasValue ? $"{cell.Value.x},{cell.Value.y}" : "?")}");
                    return true;
                };
                // the 4th chest (the Star Pendant tent's, entity 9) there before the check; after it the
                // world map keeps the camp's entrance (entity 212) and the camp is plain: its chest,
                // no walk trigger (25), none of the escape's (87-89)
                Func<bool> chest = () => { Check(FieldEntity(9) == "+", $"imperial_camp: the tent's chest there (entity 9 {FieldEntity(9)})"); return true; };
                Func<bool> entrance = () =>
                {
                    var map = UserDataManager.Instance()?.CurrentMapId;
                    Check(map == 1 && FieldEntity(212) == "+", $"imperial_camp: the world map keeps the camp's entrance (map {map}, entity 212 {FieldEntity(212)})");
                    return true;
                };
                Func<bool> plain = () =>
                {
                    Check(FieldEntity(9) == "+" && FieldEntity(25) == "-" && FieldEntity(87) == "-" && FieldEntity(88) == "-",
                          $"imperial_camp: back in after the check, the plain camp: chest {FieldEntity(9)}, walk trigger {FieldEntity(25)}, escape triggers {FieldEntity(87)}{FieldEntity(88)}");
                    return true;
                };
                return new List<(float, Func<bool>)>
                {
                    (0, recruit), (0, Warp(350, 1)), (3, atEntry), (0, chest), (0, Run("sc_e_0114")), (4, atCyan), (0, GateIs(true)),
                    (2, Run("sc_e_0128_1")), (2, Expect(true)), (5, NotBlack()), (1, entrance), (0, Warp(350, 1)), (4, plain),
                };
            }
            if (name == "narshe_moogle_defense" && req.ContainsKey("mogparty"))
            {
                // The user's Deck, 2026-11-19 ("i lost mog after doing the moogle defense check"):
                // Mog in a full active party, the defense accepted at Arvis's, then straight to the
                // Marshal's end (sc_e_0018_2: the six soldiers and the Marshal's fight skipped).
                // Party 1 must be the original four throughout, Mog in no second defense party,
                // and the party the one-party picker opens on (its screen stood in for) and keeps
                // is the original one, Mog selectable, with no guest moogle left anywhere.
                int F1(int i) => WcEvents.Flag(DataStorage.Category.kScenarioFlag1, i);
                List<int> Statuses(IEnumerable<Last.Data.User.Corps> corps)
                {
                    var owned = UserDataManager.Instance()?.OwnedCharacterList;
                    var byId = new Dictionary<int, int>();
                    if (owned != null) foreach (var o in owned) if (o != null) byId[o.Id] = o.characterStatusId;
                    return corps?.Where(x => x != null).Select(x => byId.TryGetValue(x.CharacterId, out var s) ? s : -x.CharacterId).ToList() ?? new List<int>();
                }
                List<int> Slot(UserDataManager.CorpsSlot s)
                {
                    var ud = UserDataManager.Instance();
                    if (ud?.corpsSlots == null || !ud.corpsSlots.ContainsKey((int)s)) return new List<int>();
                    return Statuses(ud.corpsSlots[(int)s].ToArray());
                }
                List<int> Active() => UserDataManager.Instance()?.GetPartyMemberData()?.ToArray().Select(p => p.characterStatusId).ToList() ?? new List<int>();
                string Main()
                {
                    try { return SceneManager.Instance?.GetCurrentSubSceneManager()?.TryCast<SubSceneManagerMainGame>()?.GetCurrentState().ToString() ?? "?"; }
                    catch (Exception e) { return "? " + e.Message; }
                }
                Last.Data.User.OwnedCharacterData MogData() =>
                    UserDataManager.Instance()?.OwnedCharacterList?.ToArray().FirstOrDefault(o => o != null && o.characterStatusId == 11);
                var original = new List<int>();
                int waits = 0;
                bool ended = false;
                Func<bool> inDefense = null, afterEnd = null;
                inDefense = () =>
                {
                    bool ready = UserDataManager.Instance()?.CurrentMapId == 318 && F1(13) == 1 && !WcEvents.EventBusy() && Main() == "Player";
                    if (!ready && ++waits < 90) { plan.Insert(0, (1f, inDefense)); return true; }
                    Check(ready, $"mogparty: the defense started (map {UserDataManager.Instance()?.CurrentMapId}, ScenarioFlag1 13 {F1(13)}, main {Main()})");
                    var p1 = Active();
                    var p2 = Slot(UserDataManager.CorpsSlot.Slot2);
                    var p3 = Slot(UserDataManager.CorpsSlot.Slot3);
                    Plugin.L.LogInfo($"[wc test] mogparty: defense parties [{string.Join(" ", p1)}] / [{string.Join(" ", p2)}] / [{string.Join(" ", p3)}] (active slot {UserDataManager.Instance()?.CorpsSlotIndex})");
                    Check(p1.SequenceEqual(original), $"mogparty: party 1 is the original party [{string.Join(" ", original)}]: [{string.Join(" ", p1)}]");
                    Check(!p2.Contains(11) && !p3.Contains(11), $"mogparty: Mog is in party 1 only (party 2 [{string.Join(" ", p2)}], party 3 [{string.Join(" ", p3)}])");
                    Check(p2.SequenceEqual(new[] { 21, 22, 23, 24 }), $"mogparty: guest 21 takes Mog's place in party 2: [{string.Join(" ", p2)}]");
                    WcEvents.PickRecruit = original[0];                        // the picker's stand-in keeps the party
                    WcEvents.PickOpenedOn = null;
                    waits = 0;
                    return true;
                };
                afterEnd = () =>
                {
                    bool done = F4(flag) == 1 && !WcEvents.EventBusy() && Main() == "Player";
                    if (!done && ++waits < 60) { plan.Insert(0, (1f, afterEnd)); return true; }
                    ended = true;
                    var owned = UserDataManager.Instance()?.OwnedCharacterList?.ToArray().Where(o => o != null).Select(o => o.characterStatusId).ToList() ?? new List<int>();
                    var slots = new[] { UserDataManager.CorpsSlot.Slot1, UserDataManager.CorpsSlot.Slot2, UserDataManager.CorpsSlot.Slot3 }.SelectMany(Slot).ToList();
                    var mog = MogData();
                    var now = Active();
                    Plugin.L.LogInfo($"[wc test] mogparty: after the end: party [{string.Join(" ", now)}], at the picker [{string.Join(" ", WcEvents.PickOpenedOn ?? new List<int>())}] " +
                                     $"(Mog selectable there {WcEvents.PickMogSelectable}), owned [{string.Join(" ", owned)}], slots [{string.Join(" ", slots)}], map {UserDataManager.Instance()?.CurrentMapId}");
                    Check(done, $"mogparty: the check rewarded and the field free (check flag {F4(flag)}, busy {WcEvents.EventBusy()}, main {Main()})");
                    Check(WcEvents.PickOpenedOn != null && WcEvents.PickOpenedOn.SequenceEqual(original),
                          $"mogparty: the one-party picker opens on the original party [{string.Join(" ", original)}]: [{string.Join(" ", WcEvents.PickOpenedOn ?? new List<int>())}]");
                    Check(WcEvents.PickMogSelectable, "mogparty: Mog is selectable when the picker opens");
                    Check(now.SequenceEqual(original), $"mogparty: the party after the defense is the original party: [{string.Join(" ", now)}]");
                    Check(mog != null && mog.IsEnableCorps, $"mogparty: Mog owned and selectable afterwards ({(mog == null ? "missing" : "selectable " + mog.IsEnableCorps)})");
                    Check(!owned.Any(s => s >= 19 && s <= 28) && !slots.Any(s => s >= 19 && s <= 28),
                          $"mogparty: no guest moogle owned or in a party (owned [{string.Join(" ", owned)}], slots [{string.Join(" ", slots)}])");
                    return true;
                };
                return new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 41, 0);
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 97, 0);
                        Recruit(11)();
                        if (!Active().Contains(11)) WcEvents.Join(11);
                        return true;
                    }),
                    (2, () =>
                    {
                        original = Active();
                        Plugin.L.LogInfo($"[wc test] mogparty: party [{string.Join(" ", original)}], Mog recruited {WcEvents.IsRecruited(11)}");
                        Check(original.Count == 4 && original.Contains(11), $"mogparty: a full party with Mog: [{string.Join(" ", original)}]");
                        WcEvents.TestAnswers.Clear();
                        WcEvents.TestAnswers.Enqueue(0);                       // (Yes.)
                        return original.Count > 0;
                    }),
                    (0, Warp(17, 18)),
                    (3, () => WcEvents.RunScript("sc_e_0017_5")),
                    (3, inDefense),
                    (0, () => WcEvents.RunScript("sc_e_0018_2")),
                    (3, afterEnd),
                    (0, () => { Check(ended, "mogparty: reached the end"); return true; }),
                };
            }
            if (name == "narshe_moogle_defense" && req.ContainsKey("play"))
            {
                // The moogle defense played from Arvis's house, as SNES WC: Mog joined (the gate),
                // the WoB, entering the mines from the south does nothing, Arvis is present in his
                // house (map 17), talking to him and choosing (Yes.) starts the opening. Then a look:
                // the game's main state (Player = the party walks, Event = a scene has it), where
                // the party is, the defense's flags; a scene's script while the state is Player
                // is the user's report. Fights are ended by force a few seconds in.
                int F1(int i) => WcEvents.Flag(DataStorage.Category.kScenarioFlag1, i);
                string Main()
                {
                    try
                    {
                        var ssm = SceneManager.Instance?.GetCurrentSubSceneManager()?.TryCast<SubSceneManagerMainGame>();
                        return ssm == null ? "?" : ssm.GetCurrentState().ToString();
                    }
                    catch (Exception e) { return "? " + e.Message; }
                }
                var seen = new List<string>();
                var originalParty = new List<int>();
                int walkable = 0, looks = 0, fightsSeen = 0;
                bool inFight = false, marshalTalked = false, rewardSeen = false, knockedOut = false, partyChecked = false;
                int afterReward = 0, afterParty = 0;
                float fightAt = -1;
                string last = "";
                var steps = new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        if (c.Gate > 0 && !WcEvents.IsRecruited(c.Gate)) Recruit(c.Gate)();
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 41, 0);
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 97, 0);
                        var lead = UserDataManager.Instance()?.GetPartyMemberData();
                        originalParty = lead?.ToArray().Select(p => p.characterStatusId).ToList() ?? new List<int>();
                        WcEvents.PickRecruit = lead != null && lead.Count > 0 ? lead[0].characterStatusId : 1;
                        WcEvents.TestAnswers.Clear();
                        WcEvents.TestAnswers.Enqueue(0);                       // (Yes.)
                        Plugin.L.LogInfo($"[wc test] moogles: party [{string.Join(" ", lead?.ToArray().Select(p => p.characterStatusId) ?? new int[0])}], gate {c.Gate} recruited {WcEvents.IsRecruited(c.Gate)}");
                        return true;
                    }),
                    (0, Warp(318, 1)),
                    (3, () =>
                    {
                        Check(!WcEvents.EventBusy() && F4(130) == 0,
                              $"moogles: entering the mines does not offer the check (busy {WcEvents.EventBusy()}, offered {F4(130)})");
                        return true;
                    }),
                    (0, Warp(17, 18)),
                    (3, () =>
                    {
                        Check(FieldEntity(12) == "+", $"moogles: Arvis is in his house (entity 12 {FieldEntity(12)})");
                        return WcEvents.RunScript("sc_e_0017_5");
                    }),
                };
                for (int k = 0; k < 720; k++)
                {
                    bool last419 = k == 719;
                    steps.Add((1f, () =>
                    {
                        bool end = last419;
                        looks++;
                        var b = BattlePlugManager.Instance();
                        float now = Time.realtimeSinceStartup;
                        if (b != null && b.IsBattle())
                        {
                            if (!inFight) { inFight = true; knockedOut = false; fightAt = now; fightsSeen++; Plugin.L.LogInfo($"[wc test] moogles: fight {fightsSeen}: party {b.MonsterParty?.Id} ({WcBattles.LastSwap})"); }
                            else if (now - fightAt > 4)
                            {
                                var j = b.BattleEndJugment;
                                if (j != null && !j.isForce)
                                {
                                    j.SetEnabled(true);
                                    j.CurrentEndJugmentType = BattleEndJugment.EndJugmentType.Normal;
                                    j.isForce = true;
                                    Plugin.L.LogInfo($"[wc test] moogles: fight {fightsSeen} ended by force");
                                }
                                else if (now - fightAt > 10 && !knockedOut)
                                {
                                    // a boss whose own script keeps the fight (the seed's Marshal: sc_b_2018's
                                    // counter loop) ignores the forced end: its monsters knocked out, a won fight
                                    knockedOut = true;
                                    var foes = b.GetEnemyUnits();
                                    int n = 0;
                                    if (foes != null)
                                        foreach (var e in foes)
                                        {
                                            var ep = e?.BattleUnitDataInfo?.Parameter;
                                            if (ep == null || ep.CurrentHP <= 0) continue;
                                            ep.CurrentHP = 0;
                                            BattleUtility.AddCondition(e, (Last.Defaine.ConditionType)5);
                                            n++;
                                        }
                                    Plugin.L.LogInfo($"[wc test] moogles: fight {fightsSeen} still on: {n} monsters knocked out");
                                }
                            }
                        }
                        else inFight = false;
                        var integ = Integrator.Instance;
                        string script = integ?.scriptName ?? "";
                        bool busy = WcEvents.EventBusy();
                        string main = Main();
                        string at;
                        try { at = $"{WcEvents.PlayerCell()}"; } catch (Exception) { at = "?"; }
                        var state = $"map {UserDataManager.Instance()?.CurrentMapId}, main {main}, script '{script}' busy {busy}, at {at}, " +
                                    $"ScenarioFlag1 13/14 {F1(13)}/{F1(14)}, offered {F4(130)}, done {F4(flag)}, battle {(inFight ? "on" : "off")}, black {ScreenBlack().Item1}";
                        if (state != last) { Plugin.L.LogInfo($"[wc test] moogles: {state}"); last = state; }
                        if (script.StartsWith("sc_e_0017") || script.StartsWith("sc_e_0018"))
                        {
                            if (!seen.Contains(script)) seen.Add(script);
                            if (busy && main == "Player") walkable++;
                        }
                        if (!partyChecked && F1(13) == 1 && UserDataManager.Instance()?.CurrentMapId == 318)
                        {
                            var nowParty = UserDataManager.Instance()?.GetPartyMemberData()?.ToArray()
                                .Select(p => p.characterStatusId).ToList() ?? new List<int>();
                            partyChecked = true;
                            Check(nowParty.Count == 4 && nowParty.Take(originalParty.Count).SequenceEqual(originalParty),
                                  $"moogles: party 1 keeps the original party [{string.Join(" ", originalParty)}], then fills with moogles: [{string.Join(" ", nowParty)}]");
                        }
                        // the six soldiers fought (sc_e_1081-1086 hide each): talk to the Marshal (entity 9 at
                        // (15, 41), sc_e_0018), as a party walked down to him does; after the reward the
                        // check flag ends the look (the one-party pick: its screen stood in for)
                        bool soldiersGone = fightsSeen >= 6 && !inFight;          // (a beaten soldier is hidden: its entity stays)
                        if (UserDataManager.Instance()?.CurrentMapId == 318 && F1(13) == 1 && !marshalTalked && !busy && main == "Player" && soldiersGone)
                        {
                            marshalTalked = true;
                            Plugin.L.LogInfo($"[wc test] moogles: soldiers gone after {fightsSeen} fights; the Marshal there: {FieldEntity(9)}; talking to him");
                            Check(WcEvents.RunScript("sc_e_0018"), "moogles: the Marshal's talk (sc_e_0018) started");
                        }
                        if (F4(flag) == 1 && !rewardSeen)
                        {
                            rewardSeen = true;
                            Plugin.L.LogInfo($"[wc test] moogles: rewarded after {fightsSeen} fights ({WcEvents.LastReward})");
                        }
                        if (rewardSeen && ++afterReward > 15) end = true;
                        if (partyChecked && ++afterParty > 5) end = true;
                        // A shuffled Marshal with a counter-loop battle script may ignore the
                        // harness's forced win even after every monster is knocked out. Reaching
                        // that seventh fight proves the complete field event; do not idle to the
                        // plan's 12-minute safety limit waiting for a battle the harness can't end.
                        if (inFight && fightsSeen >= 7 && knockedOut) end = true;
                        if (end)
                        {
                            Plugin.L.LogInfo($"[wc test] moogles: scripts seen [{string.Join(" ", seen)}], {fightsSeen} fights, {walkable} looks walkable during a scene");
                            // sc_e_0017_6 may finish between one-second samples; sc_e_0017_8 is
                            // the defense field it can reach only after _6 and the party scene.
                            Check(seen.Contains("sc_e_0017_8"), $"moogles: the defense's opening completed ([{string.Join(" ", seen)}])");
                            Check(partyChecked, "moogles: party 1 was checked after the opening");
                            Check(walkable == 0, $"moogles: no scene ran while the party could walk ({walkable} of {looks} looks)");
                            if (marshalTalked)
                                Check(fightsSeen >= 7, $"moogles: the Marshal's fight started after the six soldiers ({fightsSeen} fights)");
                            return false;
                        }
                        return true;
                    }));
                }
                return steps;
            }
            if (SimpleChecks.TryGetValue(name, out var simple))
            {
                // warp to the check's map, gate closed until its character has joined, then the scene
                var (map, script) = simple;
                // the gate is looked at when the step runs (an earlier check's reward may have
                // brought the gate character): closed while not recruited, then recruited
                Func<bool> closedThenRecruit = () =>
                {
                    if (c.Gate > 0 && !WcEvents.IsRecruited(c.Gate)) { GateIs(false)(); Recruit(c.Gate)(); }
                    return true;
                };
                var plan = new List<(float, Func<bool>)>();
                if (name == "kohlingen")
                {
                    // Enter while closed, as a save whose map initialized before its WC flags.
                    // The PR duplicated the SNES's shared inn; exercise the WoR copy that exposed
                    // this porting bug, including the loaded-inside repair path.
                    plan.Add((0, Warp(263, 1)));
                    plan.Add((3, () =>
                    {
                        // Model an older save: Setzer is owned, but its private WC gate bit is
                        // absent.  The load-time roster migration must restore it.
                        if (!WcEvents.IsRecruited(10)) WcEvents.Join(10);
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, WcEvents.Recruited + 10, 0);
                        WcEvents.RepairRecruitedFlagsFromRoster();
                        Check(WcEvents.IsRecruited(10), "kohlingen: an owned Setzer repairs the old missing gate flag");
                        WcEvents.RepairKohlingenCafe();
                        return true;
                    }));
                    plan.Add((4, () =>
                    {
                        bool found = false;
                        var fc = Object.FindObjectOfType<FieldMap>()?.fieldController;
                        for (int id = 0; id < 256 && !found; id++)
                        {
                            Last.Entity.Field.FieldEntity e = null;
                            try { e = fc?.GetFieldEntity(id); } catch { }
                            try
                            {
                                var pe = e?.Property?.TryCast<Last.Map.PropertyEvent>();
                                found = pe?.ScriptId == 709 && e.gameObject.activeInHierarchy;
                            }
                            catch { }
                        }
                        Check(found, "kohlingen: the WoR inn's reward NPC is present with recruited Setzer");
                        return true;
                    }));
                }
                else
                {
                    plan.Add((0, Warp(map, 1)));
                    plan.Add((0, closedThenRecruit));
                }
                if (name == "kohlingen") script = "sc_e_0681";
                plan.Add((0, GateIs(true)));
                plan.Add((6, Run(script)));
                plan.Add((2, Expect(true)));
                plan.Add((5, NotBlack()));
                if (name == "opera_house_wob")
                    plan.Insert(plan.Count - 1, (0f, (Func<bool>)(() =>
                    {
                        var fc = Object.FindObjectOfType<FieldMap>()?.fieldController;
                        TransportationInfo riding = null;
                        try { riding = fc?.Last_Map_IEntityAccessor_GetCurrentTransportation(); } catch { }
                        Check(UserDataManager.Instance()?.CurrentMapId == WcEvents.WobMap && riding?.Id == WcEvents.BlackjackId,
                              $"opera-end: native Blackjack flight is active (map {UserDataManager.Instance()?.CurrentMapId}, vehicle {(riding == null ? "none" : riding.Id.ToString())})");
                        return true;
                    })));
                if (name == "narshe_moogle_defense")
                    plan.Add((0, () =>
                    {
                        bool mog = false;
                        var owned = UserDataManager.Instance()?.OwnedCharacterList;
                        if (owned != null)
                            foreach (var member in owned)
                                if (member != null && member.characterStatusId == 11) { mog = true; break; }
                        Check(mog, "moogles-end: recruited Mog remains available after temporary moogles leave");
                        return true;
                    }));
                return plan;
            }
            switch (name)
            {
                case "tritoch":
                    {
                        // Narshe's cliffs (map 20) in the WoR (flags as the Falcon's wheel leaves
                        // them): the post-battle scene, no battle
                        Func<bool> wor = () =>
                        {
                            DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 97, 1);
                            DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 41, 1);
                            return true;
                        };
                        return new List<(float, Func<bool>)> { (0, wor), (0, Warp(20, 1)), (6, Run("sc_e_0780_1")), (2, Expect(true)) };
                    }
                case "zozo":
                    {
                        // the top of Zozo (map 395): gate Terra (1)
                        var p = new List<(float, Func<bool>)> { (0, Warp(395, 1)) };
                        if (c.Gate > 0 && !WcEvents.IsRecruited(c.Gate))
                            p.AddRange(new (float, Func<bool>)[] { (0, GateIs(false)), (0, Recruit(c.Gate)) });
                        p.AddRange(new (float, Func<bool>)[] { (0, GateIs(true)), (6, Run("sc_e_0303")), (2, Expect(true)) });
                        return p;
                    }
                case "kohlingen":
                    {
                        // Kohlingen's inn (map 98): gate Setzer (10)
                        var p = new List<(float, Func<bool>)> { (0, Warp(98, 1)) };
                        if (c.Gate > 0 && !WcEvents.IsRecruited(c.Gate))
                            p.AddRange(new (float, Func<bool>)[] { (0, GateIs(false)), (0, Recruit(c.Gate)) });
                        p.AddRange(new (float, Func<bool>)[] { (0, GateIs(true)), (6, Run("sc_e_0285")), (2, Expect(true)) });
                        return p;
                    }
                case "gau_father_house":
                    {
                        // Gau's father's house (map 73): gate Shadow (4); talking gives the reward
                        var p = new List<(float, Func<bool>)> { (0, Warp(73, 1)) };
                        if (c.Gate > 0 && !WcEvents.IsRecruited(c.Gate))
                            p.AddRange(new (float, Func<bool>)[] { (0, GateIs(false)), (0, Recruit(c.Gate)) });
                        p.AddRange(new (float, Func<bool>)[] { (0, GateIs(true)), (6, Run("sc_e_0097")), (2, Expect(true)) });
                        return p;
                    }
                case "ancient_castle":
                    {
                        // the stone queen (map 519): gate Edgar (5); closed until he has joined
                        // (a script started from a test runs once per map visit, even after a warp: the
                        // closed gate is checked through the gate call itself)
                        var p = new List<(float, Func<bool>)> { (0, Warp(519, 1)) };
                        if (c.Gate > 0 && !WcEvents.IsRecruited(c.Gate))
                            p.AddRange(new (float, Func<bool>)[] { (0, GateIs(false)), (0, Recruit(c.Gate)) });
                        p.AddRange(new (float, Func<bool>)[] { (0, GateIs(true)), (6, Run("sc_e_0890")), (2, Expect(true)) });
                        return p;
                    }
            }
            return null;
        }
        static int gilBefore, itemsBefore;

        // a script started on the current map with its menu answered by an option's key (the game
        // refuses a script for a while after a warp or another script: again every 2 s, up to
        // 30 s). hold = false: the plan goes on while the script runs (a take-off ends its script
        // with ChangeMap, no Exit, and the plan would wait 90 s for the scene to finish)
        static Func<bool> RunAnswering(string script, string key, string what, bool hold = true)
        {
            int tries = 0;
            Func<bool> run = null;
            run = () =>
            {
                WcEvents.TestMessages.Clear();
                WcEvents.TestSelects.Clear();
                WcEvents.TestAnswers.Clear();
                WcEvents.TestAnswerKeys.Clear();
                WcEvents.TestAnswerKeys.Enqueue(key);
                if (WcEvents.RunScript(script))
                {
                    if (!hold) WcEvents.Running = false;
                    return true;
                }
                WcEvents.TestAnswerKeys.Clear();
                if (tries == 5) WcEvents.ResetMainCore();
                if (++tries < 15) { plan.Insert(0, (2f, run)); return true; }
                Check(false, $"{what}: {script} didn't start in 30 s");
                return false;
            };
            return run;
        }

        static List<int> PartyIds()
        {
            var ids = new List<int>();
            var party = UserDataManager.Instance()?.GetPartyMemberData();
            if (party != null) foreach (var p in party) if (p != null) ids.Add(p.characterStatusId);
            return ids;
        }

        static OwnedCharacterData OwnedChar(int statusId)
        {
            var owned = UserDataManager.Instance()?.OwnedCharacterList;
            if (owned != null) foreach (var o in owned) if (o != null && o.characterStatusId == statusId) return o;
            return null;
        }

        // what a character wears (content ids)
        static List<int> Equipped(int statusId)
        {
            var list = new List<int>();
            var eq = OwnedChar(statusId)?.EquipmentData;
            // 93 and 197-200 are the game's empty-slot placeholders (content.csv MSG_SYSTEM_187/188), not gear
            if (eq != null) foreach (var kv in eq) if (kv.Value != null && kv.Value.ContentId > 0 && kv.Value.ContentId != 93 && (kv.Value.ContentId < 197 || kv.Value.ContentId > 200)) list.Add(kv.Value.ContentId);
            return list;
        }

        // every kind (equipment too) in the bag
        static int OwnedCount(int content)
        {
            int n = 0;
            var all = UserDataManager.Instance()?.GetAllOwnedItemsClone();
            if (all != null) foreach (var it in all) if (it != null && it.ContentId == content) n += it.Count;
            return n;
        }

        // the seed's starting items by content id (WcSeeds.GrantStart adds each entry)
        static Dictionary<int, int> StartItems(WcSeeds.Seed s)
        {
            var d = new Dictionary<int, int>();
            foreach (var it in s.Items) d[it[0]] = d.GetValueOrDefault(it[0]) + it[1];
            return d;
        }

        // a relic (an armor of parts group 5: the two accessory slots, Slot5 / Slot6) any character
        // may wear, Umaro included (the PR keeps him to relics)
        static int relic = -1;
        static int Relic()
        {
            if (relic > 0) return relic;
            var mm = MasterManager.Instance;
            foreach (var kv in mm.GetList<Last.Data.Master.Content>())
            {
                var ct = kv.Value;
                if (ct == null || ct.TypeId != 3 || (ct.Id >= 197 && ct.Id <= 200)) continue;   // armor, not an empty-slot placeholder
                if (mm.GetData<Last.Data.Master.Armor>(ct.TypeValue)?.PartsGroupId == 5) { relic = ct.Id; break; }
            }
            return relic;
        }

        // one relic from the bag put on a character (the game's own equip; the character's data as
        // a fallback), for the unequip test
        static string EquipRelic(int statusId)
        {
            var oc = OwnedChar(statusId);
            int r = Relic();
            if (oc == null || r < 0) return $"character {statusId}: {(oc == null ? "not owned" : "no relic in the game data")}";
            new OwnedItemClient().AddOwnedItem(r, 1);
            OwnedItemData item = null;
            foreach (var it in UserDataManager.Instance().GetAllOwnedItemsClone()) if (it != null && it.ContentId == r) item = it;
            if (item == null) return $"character {statusId}: relic {r} not in the bag";
            string how = "OwnedCharacterClient.SetEquip";
            try { new OwnedCharacterClient().SetEquip(oc.Id, Last.Defaine.EquipSlotType.Slot5, item, false, true); }
            catch (Exception e) { how = $"SetEquip threw {e.Message}"; }
            if (Equipped(statusId).Count == 0)
            {
                oc.SetEquip(Last.Defaine.EquipSlotType.Slot5, item);
                how += "; the data's SetEquip";
            }
            return $"character {statusId} wears [{string.Join(" ", Equipped(statusId))}] (relic {r}: {how})";
        }

        // WC's "Change party members." on the airships' unequip NPC (WC event/airship.py
        // unequip_party_members_npc_mod; tools/wc_events.py airship_party_npc), the Blackjack's cabin
        // (map 120, sc_e_0345): its question and menu (WC's text from Magicite), the party menu (stood
        // in for: the party's last member put in front), the leader redrawn where the party stands
        static List<(float, Func<bool>)> AirshipCrewPlan()
        {
            const int Cabin = 120;
            const string Party = "WC_AIRSHIP_PARTY";
            int want = -1, picks = 0, refreshes = 0;
            return new List<(float, Func<bool>)>
            {
                (0f, () => { Check(WcEvents.GotoMap(Cabin, 1), "airship_crew: warp to the Blackjack's cabin (map 120)"); return true; }),
                (5f, () =>
                {
                    var party = PartyIds();
                    if (party.Count < 2) { WcEvents.Join(Enumerable.Range(1, 12).First(i => !party.Contains(i))); party = PartyIds(); }
                    want = party.Last();
                    WcEvents.PickRecruit = want;
                    picks = WcEvents.PartyPicks;
                    refreshes = WcEvents.PartyRefreshes;
                    Plugin.L.LogInfo($"[wc test] airship_crew: party [{string.Join(" ", party)}]; the menu's stand-in puts {want} in front");
                    return true;
                }),
                (1f, RunAnswering("sc_e_0345", Party, "airship_crew")),
                (5f, () =>
                {
                    var menu = WcEvents.TestSelects.FirstOrDefault() ?? "";
                    Check(menu == $"{Party}/E0345_00_120_c_01/E0345_00_120_c_02/E0345_00_120_c_03", $"airship_crew: the crew member offers [{menu}]");
                    var option = MessageManager.Instance?.GetMessage(Party, false);
                    Check(WcEvents.TestMessages.Contains("WC_AIRSHIP_SERVICE: What do you need?") && option == "Change party members.",
                          $"airship_crew: WC's text from Magicite: [{string.Join(" | ", WcEvents.TestMessages)}], option '{option}'");
                    var party = PartyIds();
                    Check(WcEvents.PartyPicks == picks + 1, $"airship_crew: the party menu opened ({WcEvents.PartyPicks - picks} time(s))");
                    Check(WcEvents.PartyRefreshes == refreshes + 1 && WcEvents.PickLeader == want && party.FirstOrDefault() == want,
                          $"airship_crew: the leader redrawn ({WcEvents.PartyRefreshes - refreshes} refresh): {WcEvents.PickLeader}, party [{string.Join(" ", party)}] (want {want} in front)");
                    Check(UserDataManager.Instance()?.CurrentMapId == Cabin, $"airship_crew: still in the cabin (map {UserDataManager.Instance()?.CurrentMapId})");
                    var (black, how) = ScreenBlack();
                    Check(!black, $"airship_crew: the cabin shown after the menu: {how}");
                    return true;
                }),
            };
        }

        // -eu (WC event/airship.py equipable_umaro): the unequip NPC's two services also take Gogo's and
        // Umaro's equipment, if recruited ("those not in the party": out of it); without -eu, Gogo's
        // only (as vanilla). The Blackjack's cabin (map 120, sc_e_0345): the party filled to four so
        // Gogo and Umaro join in reserve (unless the seed started them), a relic on each, then each
        // service in a visit of its own (from the deck, 119)
        static List<(float, Func<bool>)> EuUnequipPlan()
        {
            const int Cabin = 120, Deck = 119, Gogo = 13, Umaro = 14;
            bool eu = WcSeeds.Active?.Flags.Split(' ').Contains("-eu") == true;
            var inParty = new Dictionary<int, bool>();
            Func<bool> Warp(int map, string where) => () => { Check(WcEvents.GotoMap(map, 1), $"eu_unequip: warp to {where} (map {map})"); return true; };
            Func<bool> equip = () =>
            {
                foreach (var g in new[] { Gogo, Umaro })
                {
                    if (Equipped(g).Count == 0) Plugin.L.LogInfo("[wc test] eu_unequip: " + EquipRelic(g));
                    inParty[g] = PartyIds().Contains(g);
                }
                Check(Equipped(Gogo).Count > 0 && Equipped(Umaro).Count > 0,
                      $"eu_unequip: Gogo wears [{string.Join(" ", Equipped(Gogo))}], Umaro [{string.Join(" ", Equipped(Umaro))}] " +
                      $"(in the party: Gogo {inParty[Gogo]}, Umaro {inParty[Umaro]}; -eu {(eu ? "on" : "off")})");
                return true;
            };
            Func<bool> Expect(bool all) => () =>
            {
                foreach (var g in new[] { Gogo, Umaro })
                {
                    bool covered = (g == Gogo || eu) && (all || !inParty[g]);
                    var left = Equipped(g);
                    Check((left.Count == 0) == covered,
                          $"eu_unequip ({(all ? "all members" : "those not in the party")}, -eu {(eu ? "on" : "off")}): {(g == Gogo ? "Gogo" : "Umaro")} " +
                          $"{(inParty[g] ? "in" : "out of")} the party {(covered ? "unequipped" : "keeps his")}: wears [{string.Join(" ", left)}]; {WcRecruits.LastUnequip}");
                }
                return true;
            };
            return new List<(float, Func<bool>)>
            {
                (0f, Warp(Cabin, "the Blackjack's cabin")),
                (5f, () =>
                {
                    var party = PartyIds();
                    foreach (var id in Enumerable.Range(1, 12))
                    {
                        if (party.Count >= 4) break;
                        if (party.Contains(id)) continue;
                        WcEvents.Join(id);
                        party = PartyIds();
                    }
                    foreach (var g in new[] { Gogo, Umaro }) if (!party.Contains(g)) WcEvents.Join(g);
                    Check(WcEvents.IsRecruited(Gogo) && WcEvents.IsRecruited(Umaro) && OwnedChar(Gogo) != null && OwnedChar(Umaro) != null,
                          $"eu_unequip: Gogo and Umaro recruited; party [{string.Join(" ", PartyIds())}]");
                    return true;
                }),
                (1f, equip),
                (1f, RunAnswering("sc_e_0345", "E0345_00_120_c_01", "eu_unequip")),
                (5f, Expect(false)),
                (0f, Warp(Deck, "the Blackjack's deck")),
                (5f, Warp(Cabin, "the Blackjack's cabin")),
                (5f, equip),
                (1f, RunAnswering("sc_e_0345", "E0345_00_120_c_02", "eu_unequip")),
                (5f, Expect(true)),
            };
        }

        // how many of the check's first reward the party has (espers: 0/1)
        static int RewardCount(WcSeeds.Check c, int n = 0)
        {
            if (c.Rewards.Count <= n) return 0;
            var (kind, id) = c.Rewards[n];
            var udm = UserDataManager.Instance();
            if (kind == "esper") return udm.OwnedMagicStoneList.Contains(id) ? 1 : 0;
            if (kind == "character") return WcEvents.JoinCount.GetValueOrDefault(id);   // joins (a full party puts them in reserve)
            var item = udm.SearchOwnedItem(id);
            return item?.Count ?? 0;
        }

        public static void Init()
        {
            var path = Path.Combine(BepInEx.Paths.BepInExRootPath, "wc_test.request");
            if (!File.Exists(path)) return;
            req = File.ReadAllLines(path).Where(l => l.Contains("="))
                .ToDictionary(l => l.Substring(0, l.IndexOf('=')).Trim(), l => l.Substring(l.IndexOf('=') + 1).Trim());
            try { File.Delete(path); } catch { }
            WcTestState.Running = true;
            WcTestState.RealBattleLines = req.GetValueOrDefault("msgs", "0") == "1";
            WcScreenState.Active = true;
            WcScreen.Init(Plugin.FlagDefs);
            step = Step.Wait;
            Mute();
            Plugin.L.LogInfo($"[wc test] requested: {string.Join(", ", req.Select(kv => kv.Key + "=" + kv.Value))}");
        }

        // silent run: the game's sound engine (SEAD) and Unity's audio at volume 0, every frame
        static void Mute() => TestRun.Mute();   // the modding kit's

        static bool OnTitle() =>
            Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>() != null ||
            Object.FindObjectOfType<Last.UI.Touch.TitleWindowController>() != null;

        static void Check(bool ok, string what)
        {
            if (!ok) failures++;
            Plugin.L.LogInfo($"[wc test] {(ok ? "ok  " : "FAIL")} {what}");
        }

        static string Flags(string key)
        {
            var preset = req.TryGetValue(key == "seed" ? "preset" : "preset2", out var p) ? p : req.GetValueOrDefault("preset", "");
            var flags = req.GetValueOrDefault(key == "seed" ? "flags" : "flags2", req.GetValueOrDefault("flags", ""));
            if (preset != "" && WcScreen.Defs.Presets.TryGetValue(preset, out var pf)) flags = pf + " " + flags;
            return flags.Trim();
        }

        static void Generate(string key)
        {
            var seed = req[key];
            var id = WcSeeds.NewId(seed) + "-test";
            made.Add(id);
            var err = WcGenerator.Start(seed, Flags(key), WcSeeds.DirFor(id));
            Check(err == null, $"generator started for {seed}: {err ?? "ok"}");
            stepAt = Time.realtimeSinceStartup;
        }

        // live rows and Magicite files must match the seed folder
        static void Verify(WcSeeds.Seed s)
        {
            var mm = MasterManager.Instance;
            foreach (var (table, check) in new (string, Func<string[], string>)[]
                     {
                         ("monster", c => $"{mm.GetData<Monster>(int.Parse(c[0]))?.Hp}"),
                         ("item", c => $"{mm.GetData<Item>(int.Parse(c[0]))?.Buy}"),
                         ("character_status", c => $"{mm.GetData<CharacterStatus>(int.Parse(c[0]))?.Strength}"),
                     })
            {
                var csv = Path.Combine(s.Dir, LiveData.MasterDir, table + ".csv");
                var lines = File.ReadAllLines(csv);
                var header = lines[0].Split(',');
                var col = table == "monster" ? "hp" : table == "item" ? "buy" : "strength";
                int ci = Array.IndexOf(header, col);
                int checkedRows = 0, bad = 0;
                foreach (var line in lines.Skip(1).Take(60))
                {
                    var c = line.Split(',');
                    if (c.Length <= ci) continue;
                    checkedRows++;
                    if (check(c) != c[ci]) { if (bad++ < 3) Plugin.L.LogInfo($"[wc test]   {table} {c[0]}: live {check(c)}, seed {c[ci]}"); }
                }
                Check(bad == 0 && checkedRows > 0, $"{table}.{col}: {checkedRows} rows match the seed ({bad} differ)");
            }
            var mod = MagiciteDir.FolderPath("WorldsCollide");
            int same = 0, diff = 0;
            foreach (var rel in s.Files)
            {
                var a = File.ReadAllBytes(Path.Combine(s.Dir, "files", rel));
                var b = File.Exists(Path.Combine(mod, rel)) ? File.ReadAllBytes(Path.Combine(mod, rel)) : null;
                if (b != null && a.SequenceEqual(b)) same++; else diff++;
            }
            Check(diff == 0, $"Magicite files: {same} of {s.Files.Length} match the seed");
        }

        static System.Action recorder;   // a plan's per-frame record (doma_play)
        static int recorderErrors;

        public static void Update()
        {
            if (step == Step.Off || step == Step.Done) return;
            Mute();
            WalkTick();
            if (recorder != null) { try { recorder(); } catch (Exception e) { if (recorderErrors++ < 5) Plugin.L.LogWarning($"[wc test] recorder: {e.Message}"); } }
            float now = Time.realtimeSinceStartup;
            switch (step)
            {
                case Step.Wait:
                    if (!LiveData.Ready || !OnTitle()) return;
                    if (since < 0) since = now;
                    if (now - since < 3) return;
                    prevActive = WcSeeds.Active?.Id;
                    Plugin.L.LogInfo($"[wc test] on the title screen; WC mode {Plugin.WcMode}; active seed {prevActive ?? "none"}; " +
                                     $"monster 1 HP {MasterManager.Instance.GetData<Monster>(1)?.Hp}");
                    if (!Plugin.WcMode) { Check(false, "Worlds Collide isn't the active mod (Magicite import directory)"); Finish(); return; }
                    if (req.GetValueOrDefault("maps", "0") == "1")
                    {
                        var mgr = SaveSlotManager.Instance();
                        var rows = new List<string>();
                        if (mgr?.SaveSlotDataList != null)
                            foreach (var sd in mgr.SaveSlotDataList)
                            {
                                if (sd == null || string.IsNullOrEmpty(sd.TimeStamp)) continue;
                                var match = System.Text.RegularExpressions.Regex.Match(sd.MapData ?? "", "\\\"mapId\\\"\\s*:\\s*(-?\\d+)");
                                rows.Add($"{sd.Id}:{(match.Success ? match.Groups[1].Value : "?")}:{sd.CurrentArea}/{sd.CurrentLocation}");
                            }
                        var latest = mgr?.GetLatestSaveSlotData(false, false);
                        if (latest != null)
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(latest.MapData ?? "", "\\\"mapId\\\"\\s*:\\s*(-?\\d+)");
                            rows.Add($"latest={latest.Id}:{(match.Success ? match.Groups[1].Value : "?")}:{latest.CurrentArea}/{latest.CurrentLocation}");
                        }
                        Plugin.L.LogInfo("[wc test] save maps: " + string.Join(" | ", rows));
                        Finish();
                        return;
                    }
                    if (req.ContainsKey("inspect") || req.ContainsKey("checkpoint"))
                    {
                        step = Step.Load;
                        return;
                    }
                    if (req.GetValueOrDefault("startup", "0") == "1")
                    {
                        step = Step.Startup;
                        stepAt = now;
                        return;
                    }
                    try { Check(true, "screen logic: " + WcScreen.SelfTest()); }
                    catch (Exception e) { Check(false, "screen logic: " + e); }
                    TitleMenuCheck();
                    if (req.GetValueOrDefault("menu", "0") == "1") { Finish(); return; }  // the menu checks only
                    Generate("seed");
                    step = Step.Gen1;
                    return;
                case Step.Startup:
                    {
                        // a seed active at startup (run_tests.ps1 sets it before the game starts): its
                        // tables applied with no failures, its data in the game (each character's
                        // command too: the user's seed had vanilla commands after a restart)
                        var s = WcSeeds.Active;
                        if (s == null) { Check(false, "startup: no active seed"); Finish(); return; }
                        if (!WcSeeds.StartupDone)
                        {
                            if (now - stepAt > 90) { Check(false, $"startup apply never finished: {WcSeeds.LastApply}"); Finish(); }
                            return;
                        }
                        Check(WcSeeds.StartupFailed == 0 && !WcSeeds.LastApply.Contains(" 0 rows set"),
                              $"startup apply: {WcSeeds.LastApply}");
                        Verify(s);
                        var csv = File.ReadAllLines(Path.Combine(s.Dir, LiveData.MasterDir, "character_status.csv"));
                        var header = csv[0].Split(',');
                        int c3 = Array.IndexOf(header, "command_id3"), bad = 0, seen = 0;
                        foreach (var line in csv.Skip(1))
                        {
                            var c = line.Split(',');
                            if (!int.TryParse(c[0], out int cid) || cid < 1 || cid > 14 || c.Length <= c3) continue;
                            seen++;
                            int live = MasterManager.Instance.GetData<CharacterStatus>(cid)?.CommandId3 ?? -1;
                            if (live.ToString() != c[c3]) { bad++; Plugin.L.LogInfo($"[wc test]   character {cid}: command 3 live {live}, seed {c[c3]}"); }
                        }
                        Check(seen > 0 && bad == 0, $"startup: every character's command is the seed's ({seen} checked, {bad} differ)");
                        Finish();
                        return;
                    }
                case Step.Gen1:
                case Step.Gen2:
                    if (WcGenerator.Running)
                    {
                        if (now - stepAt > 120) { Check(false, "generator timed out"); WcGenerator.Cancel(); Finish(); }
                        return;
                    }
                    var id = made.Last();
                    if (WcSeeds.Active?.Id != id)
                    {
                        if (now - stepAt > 125) { Check(false, $"seed {id} wasn't applied: {WcGenerator.Log.Split('\n').LastOrDefault()}"); Finish(); }
                        return;
                    }
                    Check(true, $"seed {id} generated in {now - stepAt:0.0} s and applied: {WcSeeds.LastApply}");
                    Verify(WcSeeds.Active);
                    if (step == Step.Gen1 && req.ContainsKey("seed2")) { Generate("seed2"); step = Step.Gen2; return; }
                    if (req.GetValueOrDefault("delete", "0") == "1") { DeleteChecks(); Finish(); return; }
                    step = req.GetValueOrDefault("newgame", "0") == "1" ? Step.NewGame : Step.Vanilla;
                    return;
                case Step.Vanilla:
                    {
                        var err = WcSeeds.Apply(null);
                        Check(err == null, $"back to the game's own data: {err ?? WcSeeds.LastApply}");
                        var monster = File.ReadAllLines(Path.Combine(WcDataDir.Baseline, "vanilla_data", LiveData.MasterDir, "monster.csv"));
                        int hpCol = Array.IndexOf(monster[0].Split(','), "hp");
                        var first = monster[1].Split(',');
                        Check(MasterManager.Instance.GetData<Monster>(int.Parse(first[0]))?.Hp.ToString() == first[hpCol],
                              $"monster {first[0]} HP back to {first[hpCol]}");
                        step = req.GetValueOrDefault("load", "0") == "1" ? Step.Load : Step.Done;
                        if (step == Step.Done) Finish();
                        return;
                    }
                case Step.Load:
                    {
                        var mgr = SaveSlotManager.Instance();
                        SaveSlotData slotData = null;
                        if (int.TryParse(req.GetValueOrDefault("inspect", ""), out int inspectSlot))
                            slotData = mgr?.GetTargetSaveSlotData(inspectSlot);
                        else
                            slotData = mgr?.GetLatestSaveSlotData(false, false);
                        if (slotData == null && req.ContainsKey("inspect") && mgr?.SaveSlotDataList != null)
                        {
                            var listed = new List<string>();
                            foreach (var sd in mgr.SaveSlotDataList)
                                if (sd != null && !string.IsNullOrEmpty(sd.TimeStamp))
                                    listed.Add($"{sd.Id}:{sd.TimeStamp}:{sd.CurrentArea}/{sd.CurrentLocation}");
                            Plugin.L.LogInfo($"[wc test] inspect available saves: {(listed.Count == 0 ? "none" : string.Join(" | ", listed))}");
                            slotData = mgr.GetLatestSaveSlotData(false, false);
                            if (slotData != null) Plugin.L.LogInfo($"[wc test] inspect requested slot was absent; latest is {slotData.Id}");
                        }
                        if (slotData == null) { Check(false, "no save to load"); Finish(); return; }
                        loadedSlotData = slotData;
                        var title = Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>();
                        if (title != null)
                        {
                            // the title's own load, all of it (PrKit ModSaves.LoadAtTitle: its fade-out and map-load pump;
                            // without them this path read the save and stalled, and ~74 s later the title's idle demo
                            // field passed for the loaded one, 2026-11-20); "[saves] save N read; loading its map M"
                            Plugin.L.LogInfo($"[wc test] loading save slot {slotData.Id} the title's way (ModSaves.LoadAtTitle)");
                            var why = ModSaves.LoadAtTitle(slotData);
                            if (why != null) { Check(false, $"save slot {slotData.Id}: {why}"); Finish(); return; }
                        }
                        else
                        {
                            Plugin.L.LogInfo($"[wc test] loading save slot {slotData.Id} (GotoLoadSaveData, hooked)");
                            mgr.GotoLoadSaveData(slotData);
                        }
                        stepAt = now;
                        step = Step.Loading;
                        return;
                    }
                case Step.Loading:
                    if (Object.FindObjectOfType<FieldMap>() != null)
                    {
                        Check(true, $"save loaded to the field after the hook ({now - stepAt:0.0} s)");
                        if (req.GetValueOrDefault("checkpoint", "") == "opera")
                        {
                            // A native map-112 payload is captured separately from a clean New Game;
                            // map payloads are map-specific and changing mapId inside map 114's JSON
                            // produces a save that the PR cannot restore.
                            string template = Path.Combine(Plugin.Folder, "opera_map112.json");
                            if (!File.Exists(template)) { Check(false, "Opera checkpoint: native map-112 template is missing"); Finish(); return; }
                            var ds = DataStorage.Instance;
                            foreach (int flag in new[] { 160, 161, 162, 163, 164, 165, 167, 174, 175, 176, 177 })
                                ds?.Set(DataStorage.Category.kScenarioFlag1, flag, 0);
                            ds?.Set(DataStorage.Category.kScenarioFlag4, 74, 0);
                            ds?.Set(DataStorage.Category.kScenarioFlag4, 60, 0);
                            int slot = int.TryParse(req.GetValueOrDefault("slot", "20"), out int wanted) ? wanted : 20;
                            loadedSlotData.DataStorage = ds.Serialize();
                            loadedSlotData.MapData = File.ReadAllText(template);
                            loadedSlotData.Id = slot;
                            var result = SaveSlotManager.Instance()?.Save(slot, loadedSlotData);
                            Plugin.L.LogInfo($"[wc test] Opera checkpoint template save result: {result}");
                            Check(result != null, $"Opera checkpoint saved to slot {slot}");
                            Finish();
                            return;
                        }
                        if (req.ContainsKey("inspect"))
                        {
                            var done = new List<string>();
                            var left = new List<string>();
                            foreach (var c in WcTrack.Checks)
                            {
                                if (c.Flag < 0) continue;
                                int value = WcEvents.Flag(DataStorage.Category.kScenarioFlag4, c.Flag);
                                (value == 1 ? done : left).Add($"{c.Flag}:{c.Name}");
                            }
                            Plugin.L.LogInfo($"[wc test] inspect sentinel flags: WC={WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 0)}, " +
                                             $"Tzen={WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 1)}, " +
                                             $"MoogleDefense={WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 62)}, " +
                                             $"MogRecruited={WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 111)}");
                            Plugin.L.LogInfo($"[wc test] inspect completed ({done.Count}): {string.Join(" | ", done)}");
                            Plugin.L.LogInfo($"[wc test] inspect unfinished ({left.Count}): {string.Join(" | ", left)}");
                            Finish();
                            return;
                        }
                        // a New Game's WC start state, granted here to the loaded game (memory only)
                        var seed = made.Count > 0 ? WcSeeds.Load(made[0]) : null;
                        var udm = UserDataManager.Instance();
                        if (seed != null && udm != null)
                        {
                            int gil = udm.OwendGil;
                            var startItems = StartItems(seed);
                            var had = startItems.Keys.ToDictionary(k => k, OwnedCount);
                            var msg = WcSeeds.GrantStart(seed);
                            Check(msg != null && udm.OwendGil == Math.Min(9999999, gil + seed.Gold),
                                  $"start state granted: {msg ?? "failed"}; gil {gil} -> {udm.OwendGil} (seed {seed.Gold}), items {string.Join(" ", seed.Items.Select(i => $"{i[0]}x{i[1]}"))}");
                            // each of the seed's items added (a stack stops at 99), every one of its espers owned
                            var short_ = startItems.Where(kv => OwnedCount(kv.Key) != Math.Min(99, had[kv.Key] + kv.Value))
                                                   .Select(kv => $"content {kv.Key}: {had[kv.Key]} -> {OwnedCount(kv.Key)} (+{kv.Value})").ToList();
                            Check(short_.Count == 0, $"start items granted: {startItems.Count} kinds{(short_.Count > 0 ? ": " + string.Join("; ", short_) : "")}");
                            var noStone = seed.Espers.Where(e => !udm.OwnedMagicStoneList.Contains(e)).ToList();
                            Check(noStone.Count == 0, $"start espers granted: {seed.Espers.Count}{(noStone.Count > 0 ? ", missing " + string.Join(" ", noStone) : "")}");
                            // battle with that seed active (scaling); the install is put back in Finish
                            if (req.GetValueOrDefault("battle", "0") == "1" && WcSeeds.Apply(seed) == null)
                            {
                                stepAt = now;
                                step = Step.Battle;
                                return;
                            }
                        }
                        Finish();
                    }
                    else if (now - stepAt > 150) { Check(false, "the save didn't reach the field in 150 s"); Finish(); }
                    return;
                case Step.OperaCheckpoint:
                    if (operaCheckpointPhase == 0)
                    {
                        // Leave the completed Opera scene with its flags intact. Clearing them on
                        // map 114 while its interpreter was still winding down prevented the native
                        // transition from completing.
                        if (now - stepAt < 3 || WcEvents.EventBusy()) return;
                        Check(WcEvents.GotoMap(112, 1), "Opera checkpoint: warp to the Opera House entrance");
                        operaCheckpointPhase = 1;
                        stepAt = now;
                        return;
                    }
                    if (Object.FindObjectOfType<FieldMap>() == null || (UserDataManager.Instance()?.CurrentMapId ?? -1) != 112) return;
                    if (operaCheckpointPhase == 1)
                    {
                        // Now that map 112 owns the live map state, undo only the Opera story/check
                        // flags. WC's New Game deliberately starts with 158/159/166, so keep those.
                        var ds = DataStorage.Instance;
                        foreach (int flag in new[] { 160, 161, 162, 163, 164, 165, 167, 174, 175, 176, 177 })
                            ds?.Set(DataStorage.Category.kScenarioFlag1, flag, 0);
                        ds?.Set(DataStorage.Category.kScenarioFlag4, 74, 0); // Perform In Opera objective
                        ds?.Set(DataStorage.Category.kScenarioFlag4, 60, 0); // Opera disruption check
                        operaCheckpointPhase = 2;
                        stepAt = now;
                        Plugin.L.LogInfo("[wc test] Opera checkpoint: entrance loaded; story flags reset");
                        return;
                    }
                    // Let the native map transition finish constructing map-specific save state.
                    if (now - stepAt < 3 || WcEvents.EventBusy() || operaSaveStarted) return;
                    operaSaveStarted = true;
                    {
                        int slot = int.TryParse(req.GetValueOrDefault("slot", "20"), out int wanted) ? wanted : 20;
                        Action<bool> saved = ok =>
                        {
                            Check(ok, $"Opera checkpoint saved to slot {slot}");
                            Finish();
                        };
                        Plugin.L.LogInfo($"[wc test] Opera checkpoint: native save on map 112 to slot {slot}");
                        SaveSlotManager.Instance()?.Save(slot, saved, false);
                    }
                    return;
                case Step.NewGame:
                    {
                        var title = Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>();
                        var set = typeof(Last.UI.KeyInput.TitleWindowController).GetMethod("SetCommand",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (title == null || set == null) { Check(false, "New Game: no title window / SetCommand"); Finish(); return; }
                        var ps = set.GetParameters();
                        Plugin.L.LogInfo($"[wc test] New Game with seed {WcSeeds.Describe(WcSeeds.Active)}: start party " +
                                         $"[{string.Join(" ", WcSeeds.Active.StartCharacters)}], gil {WcSeeds.Active.Gold}");
                        if (req.GetValueOrDefault("trace", "0") == "1") WcEvents.Trace = 300;   // the opening's instructions
                        // the title's own New Game command (an earlier version passed id 0, which
                        // started the opening as the attract demo does and hid a WC start bug)
                        WcEvents.NewGameChosenAt = Time.realtimeSinceStartup;
                        set.Invoke(title, new object[] { Last.Defaine.TitleCommandId.NewGame, Enum.ToObject(ps[1].ParameterType, 2), false, true });
                        stepAt = now;
                        lastReport = "";
                        step = Step.InNewGame;
                        return;
                    }
                case Step.InNewGame:
                    {
                        var udm = UserDataManager.Instance();
                        var report = udm != null && Object.FindObjectOfType<FieldMap>() != null ? WcEvents.Report() : "not in the field";
                        if (report != lastReport) { Plugin.L.LogInfo($"[wc test]   {now - stepAt:0.0} s: {report}"); lastReport = report; }
                        if (udm != null && udm.CurrentMapId == 119 && now - stepAt > 3 && Object.FindObjectOfType<FieldMap>() != null)
                        {
                            if (deckAt < 0) deckAt = now;
                            if (now - deckAt < 4) return;   // let the arrival script finish
                            var seed = WcSeeds.Active;
                            var party = new List<int>();
                            foreach (var c in udm.GetPartyMemberData()) party.Add(c.characterStatusId);
                            Check(party.OrderBy(x => x).SequenceEqual(seed.StartCharacters.OrderBy(x => x)),
                                  $"New Game party [{string.Join(" ", party)}] = the seed's [{string.Join(" ", seed.StartCharacters)}]");
                            Check(udm.OwendGil == seed.Gold, $"gil {udm.OwendGil} = the seed's {seed.Gold}");
                            {
                                // the seed's starting items and espers (WcSeeds.GrantStart at WC's start):
                                // each item at least the seed's count and at most that plus what else a
                                // New Game may hold of it (the PR's own start item, initialize_data
                                // INIT_ITEM_n; item gifts of objectives met at the start), so a missing or
                                // second grant shows; exactly the seed's espers
                                var want = StartItems(seed);
                                var also = new Dictionary<int, int>();
                                foreach (var kv in MasterManager.Instance.GetList<Last.Data.Master.InitializeData>())
                                    if (kv.Value != null && (kv.Value.KeyName ?? "").StartsWith("INIT_ITEM_"))
                                        also[kv.Value.Value1] = also.GetValueOrDefault(kv.Value.Value1) + kv.Value.Value2;
                                foreach (var o in seed.Objectives)
                                    foreach (var it in o.Items) also[it[0]] = also.GetValueOrDefault(it[0]) + it[1];
                                var itemsBad = new List<string>();
                                foreach (var kv in want)
                                {
                                    int have = OwnedCount(kv.Key), most = kv.Value + also.GetValueOrDefault(kv.Key);
                                    if (have < kv.Value || have > most) itemsBad.Add($"content {kv.Key}: {have} (the seed's {kv.Value}{(most > kv.Value ? $", up to {most}" : "")})");
                                }
                                Check(itemsBad.Count == 0, $"start items: the seed's {want.Count} kinds, {want.Values.Sum()} in all, in the bag" +
                                      (itemsBad.Count > 0 ? ": " + string.Join("; ", itemsBad) : $" [{string.Join(" ", want.Select(kv => $"{kv.Key}x{kv.Value}"))}]"));
                                var stones = new List<int>();
                                var owned = udm.OwnedMagicStoneList;
                                for (int i = 0; owned != null && i < owned.Count; i++) stones.Add(owned[i]);
                                var seedStones = seed.Espers.Distinct().OrderBy(x => x).ToList();
                                Check(stones.OrderBy(x => x).SequenceEqual(seedStones),
                                      $"start espers [{string.Join(" ", stones.OrderBy(x => x))}] = the seed's [{string.Join(" ", seedStones)}]");
                            }
                            Check(report.Contains("Blackjack map 1 at 84,34"), $"on the Blackjack's deck: {report}; {WcEvents.LastStart}");
                            Check(report.Contains("WC game 1"), "WC game flag set (ScenarioFlag4 0)");
                            Check(WcEvents.Flag(DataStorage.Category.kScenarioFlag2, WcEvents.CollapseFlag) == 0,
                                  $"a WC start in the World of Balance: 'after the collapse' (ScenarioFlag2 41) = {WcEvents.Flag(DataStorage.Category.kScenarioFlag2, WcEvents.CollapseFlag)} (double Morph by Morph_Postfix, not that flag)");
                            int SF(DataStorage.Category k, int i) => WcEvents.Flag(k, i);
                            Check(SF(DataStorage.Category.kScenarioFlag1, 158) == 1 && SF(DataStorage.Category.kScenarioFlag1, 201) == 1 &&
                                  SF(DataStorage.Category.kScenarioFlag2, 1) == 1 && SF(DataStorage.Category.kScenarioFlag3, 36) == 1 &&
                                  SF(DataStorage.Category.kScenarioFlag1, 194) == 0 && SF(DataStorage.Category.kScenarioFlag1, 64) == 0,
                                  "WC start flags (opera overture, no coin toss, Gestahl's dinner, no chocobo tutorial; not 194/64)");
                            // every character at full HP / MP, as the SNES start has them (the user's
                            // Deck seed, 2026-11-06: current HP under max; Mesen read the SNES game's)
                            var hpAll = new List<string>();
                            var hpBad = new List<string>();
                            foreach (var c in udm.OwnedCharacterList)
                            {
                                if (c == null || c.characterStatusId < 1 || c.characterStatusId > 14) continue;
                                var p = c.Parameter;
                                var row = MasterManager.Instance.GetData<CharacterStatus>(c.characterStatusId);
                                var s = $"{c.characterStatusId}: Lv {p.BaseLevel} HP {p.CurrentHP}/{p.ConfirmedMaxHp()} (base {p.BaseMaxHp} +{p.AddtionalMaxHp} equip {p.EquipMaxHp}; table {row?.Hp}) " +
                                        $"MP {p.CurrentMP}/{p.ConfirmedMaxMp()} (base {p.BaseMaxMp} +{p.AddtionalMaxMp} equip {p.EquipMaxMp}; table {row?.Mp})";
                                hpAll.Add(s);
                                if (p.CurrentHP != p.ConfirmedMaxHp() || p.CurrentMP != p.ConfirmedMaxMp()) hpBad.Add(s);
                            }
                            Plugin.L.LogInfo("[wc test] start HP/MP: " + string.Join("; ", hpAll));
                            var (deckBlack, deckFade) = ScreenBlack();
                            Check(!deckBlack, $"the deck at the start isn't black: {deckFade}");
                            Check(hpAll.Count > 0 && hpBad.Count == 0, $"the start's characters at full HP/MP: {(hpBad.Count == 0 ? $"{hpAll.Count} characters" : string.Join("; ", hpBad))}");
                            // MagiTek dealt as a command (WC's -com): the game lists and allows Magitek (26)
                            // for those characters only, in one slot; a recruited Terra has WC's 4 basic
                            // moves (her other 4 come with "Magitek Upgrade")
                            if (seed.Magitek.Count > 0)
                            {
                                var cmd26 = MasterManager.Instance.GetData<Command>(WcMagitek.Command);
                                var cad = new Serial.FF6.Management.CommandAvailableData();
                                var rep = new List<string>();
                                bool tekOk = cmd26 != null;
                                foreach (var c in udm.OwnedCharacterList)
                                {
                                    if (c == null || c.characterStatusId < 1 || c.characterStatusId > 14 || cmd26 == null) continue;
                                    bool listed = cad.IsCommandRelease(c, cmd26), usable = cad.IsAvailable(c, cmd26,
                                        udm.GetPartyMemberData().Cast<Il2CppSystem.Collections.Generic.IEnumerable<Last.Data.User.OwnedCharacterData>>(),
                                        udm.GetOwnedItemsClone().Cast<Il2CppSystem.Collections.Generic.IEnumerable<Last.Data.User.OwnedItemData>>());
                                    bool want = seed.Magitek.Contains(c.characterStatusId);
                                    int slots = 0;
                                    if (c.CommandList != null) foreach (var x in c.CommandList) if (x?.Id == WcMagitek.Command) slots++;
                                    rep.Add($"{c.characterStatusId}: {(listed ? "listed" : "hidden")}{(usable ? " usable" : "")} {slots} slot(s)");
                                    if (listed != want || (want && (slots != 1 || !usable))) tekOk = false;
                                }
                                Check(tekOk, $"MagiTek dealt to [{string.Join(" ", seed.Magitek)}]: {string.Join("; ", rep)}");
                            }
                            var terra = udm.OwnedCharacterList.ToArray().FirstOrDefault(c => c != null && c.characterStatusId == 1);
                            // only the seed's starters are recruited: the opening's Terra stays owned
                            // (unselectable) when she isn't one, and the roster repair once flagged her
                            // (seed gaadl68ix7j4, start party [5 3 12])
                            var recruited = Enumerable.Range(1, 14).Where(WcEvents.IsRecruited).ToList();
                            Check(recruited.OrderBy(i => i).SequenceEqual(seed.StartCharacters.OrderBy(i => i)),
                                  $"recruited at the start = the seed's starters: [{string.Join(" ", recruited)}]" +
                                  (terra != null ? $" (Terra owned, selectable {terra.IsEnableCorps})" : ""));
                            // the starters know exactly the seed's natural magic for their level (WcSpells:
                            // the PR builds Cure / Fire into Terra whoever learns her list)
                            var spellsBad = new List<string>();
                            foreach (var c in udm.OwnedCharacterList)
                            {
                                if (c == null || c.characterStatusId < 1 || c.characterStatusId > 14 || !WcEvents.IsRecruited(c.characterStatusId)) continue;
                                var (unearned, missing, _) = SpellState(c);
                                Plugin.L.LogInfo("[wc test]   start spells " + SpellText(c));
                                if (unearned.Count > 0 || missing.Count > 0) spellsBad.Add(SpellText(c));
                            }
                            Check(spellsBad.Count == 0, $"the starters know exactly their natural magic{(spellsBad.Count > 0 ? ": " + string.Join("; ", spellsBad) : "")}");
                            if (terra != null && !seed.StartCharacters.Contains(1))
                            {
                                // a save the old repair touched: the flag goes again, once the field is idle
                                DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, WcEvents.Recruited + 1, 1);
                                bool busy = WcEvents.EventBusy();
                                WcEvents.RepairRecruitedFlagsFromRoster();
                                Check(!WcEvents.IsRecruited(1), $"the opening's Terra, flagged by an old repair, is un-flagged (event busy {busy})");
                                DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, WcEvents.Recruited + 1, 0);
                            }
                            if (terra != null && WcEvents.IsRecruited(1))
                            {
                                bool Has(int id) => WcSkills.Knows(terra, id);
                                Check(WcObjectives.MagitekBasic.All(Has) && !WcObjectives.MagitekTerra.Any(Has),
                                      $"Terra's Magitek moves are WC's basic 4: [{string.Join(" ", WcObjectives.MagitekBasic.Concat(WcObjectives.MagitekTerra).Where(Has))}]");
                            }
                            // research (MagiTek as a normal command): the Magitek moves (abilities 218-225)
                            // each owned character has at the start, and its commands
                            foreach (var c in udm.OwnedCharacterList)
                            {
                                if (c == null || c.characterStatusId < 1 || c.characterStatusId > 14) continue;
                                var tek = new List<string>();
                                if (c.OwnedAbilityList != null)
                                    foreach (var a in c.OwnedAbilityList)
                                        if (a?.Ability != null && a.Ability.Id >= 218 && a.Ability.Id <= 225) tek.Add($"{a.Ability.Id}@{a.SkillLevel}");
                                var cmds = new List<string>();
                                if (c.CommandList != null) foreach (var x in c.CommandList) if (x != null) cmds.Add(x.Id.ToString());
                                Plugin.L.LogInfo($"[wc test]   magitek {c.characterStatusId}: moves [{string.Join(" ", tek)}], commands [{string.Join(" ", cmds)}]");
                            }
                            // the start's rages / lores / dances are the seed's (-srr / -slr / -sdr), no
                            // more (the user's Deck seed began with every rage: the PR's own defaults)
                            foreach (var (kind, cmd, lo, hi) in new[] { ("rages", 23, 800, 1054), ("lores", 17, 140, 163), ("dances", 22, 164, 171) })
                            {
                                if (!seed.StartSkills.TryGetValue(kind, out var all)) continue;
                                // the PR has no ability for three SNES rages (855, 864, 987): those can't be known
                                var want = all.Where(id => MasterManager.Instance.GetData<Ability>(id) != null).ToList();
                                var holder = WcSkills.Holder(cmd);
                                if (holder == null) { Plugin.L.LogInfo($"[wc test]   start {kind}: nobody holds command {cmd}"); continue; }
                                var known = new HashSet<int>();
                                foreach (var a in holder.OwnedAbilityList)
                                    if (a?.Ability != null && a.Ability.Id >= lo && a.Ability.Id <= hi && a.SkillLevel >= 100) known.Add(a.Ability.Id);
                                Check(known.SetEquals(want), $"start {kind}: character {holder.characterStatusId} knows {known.Count}, the seed's {want.Count}" +
                                      $" (extra {string.Join(",", known.Except(want).Take(8))}; missing {string.Join(",", want.Except(known).Take(8))})");
                            }
                            var wor = MessageManager.Instance?.GetMessage("WC_HELM_WOR", false);
                            Check(wor == "(Fly to the World of Ruin.)", $"WC text from Magicite: WC_HELM_WOR = '{wor}'");
                            if (seed.Flags.Split(' ').Contains("-etr"))
                                Check(seed.Timers.Count == 3 && seed.Timers.Values.All(v => v >= 60 && v <= 600),
                                      $"-etr: the seed's timers {string.Join(", ", seed.Timers.Select(kv => $"{kv.Key} {kv.Value} s"))}");
                            if (seed.Flags.Contains("-name "))
                            {
                                var named = new List<string>();
                                foreach (var c in udm.GetPartyMemberData()) named.Add($"{c.characterStatusId}:{c.Name}");
                                var want = Tokens.Split(seed.Flags)[Tokens.Split(seed.Flags).IndexOf("-name") + 1].Split('.');
                                Check(udm.GetPartyMemberData().ToArray().All(c => want[c.characterStatusId - 1] == "" || c.Name == want[c.characterStatusId - 1]),
                                      $"-name: party [{string.Join(" ", named)}]");
                            }
                            if (seed.Flags.Split(' ').Contains("-scan"))
                            {
                                // -scan/-warp: the start party knows Libra (64) / Teleport (82) at 100%
                                var known = new List<string>();
                                foreach (var c in udm.GetPartyMemberData())
                                {
                                    bool Has(int id) { foreach (var a in c.OwnedAbilityList) if (a?.Ability?.Id == id && a.SkillLevel >= 100) return true; return false; }
                                    known.Add($"{c.characterStatusId}:{(Has(64) ? "L" : "-")}{(Has(82) ? "T" : "-")}");
                                }
                                bool warp = seed.Flags.Split(' ').Contains("-warp");
                                Check(known.All(k => k.EndsWith(warp ? "LT" : "L-")), $"-scan{(warp ? "/-warp" : "")}: party abilities [{string.Join(" ", known)}]");
                            }
                            if (req.GetValueOrDefault("worlds", "0") == "1")
                            {
                                WcEvents.RunScript("sc_e_BJoutboard", 1);   // (Fly to the World of Ruin.)
                                stepAt = now;
                                step = Step.ToWor;
                                return;
                            }
                            if (req.GetValueOrDefault("coliseum", "0") == "1")
                            {
                                // WC's Coliseum: the game's lookups answer from the seed; the game's own
                                // answers match the vanilla table (the id convention: PR monster = SNES + 1)
                                var data = new Serial.FF6.Management.ColosseumInfomationData();
                                int same = 0, ours = 0, n = 0;
                                foreach (var kv in seed.Coliseum.Take(30))
                                {
                                    int mon = data.GetMonsterId(kv.Key), rew = data.GetRewardItemId(kv.Key);
                                    n++;
                                    bool fights = true;
                                    if (kv.Value[2] == 0 && kv.Value[5] > 0)
                                    {
                                        var colParty = MasterManager.Instance.GetData<MonsterParty>(mon);
                                        fights = colParty != null && new[] { colParty.Monster1, colParty.Monster2, colParty.Monster3, colParty.Monster4, colParty.Monster5, colParty.Monster6 }.Contains(kv.Value[5]);
                                    }
                                    if ((kv.Value[2] > 0 ? mon == kv.Value[2] : mon == kv.Value[0] && fights) && rew == kv.Value[3]) ours++;
                                    if (WcColiseum.GameMonster == kv.Value[0] && WcColiseum.GameReward == kv.Value[1]) same++;
                                    else Plugin.L.LogInfo($"[wc test] coliseum: the game's own differs from the vanilla table at wager {kv.Key}: {WcColiseum.Last}");
                                    if (n <= 3) Plugin.L.LogInfo("[wc test] coliseum " + WcColiseum.Last);
                                }
                                Check(n > 0 && ours == n, $"coliseum: {ours} of {n} wagers answered from the seed");
                                Check(n > 0 && same >= n - 2, $"coliseum: the game's own {same} of {n} match the vanilla table (ids line up; the PR has its own special matches)");
                                Finish();
                                return;
                            }
                            if (req.TryGetValue("narshe", out var narsheWorld))
                            {
                                plan = NarshePlan(narsheWorld == "wor");
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.GetValueOrDefault("worldsync", "0") == "1")
                            {
                                plan = WorldSyncPlan();
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.TryGetValue("tour", out var tourWorld))
                            {
                                plan = TourPlan(tourWorld == "wor");
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.GetValueOrDefault("template", "") == "opera")
                            {
                                plan = OperaTemplatePlan();
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.GetValueOrDefault("rooms", "0") == "1")
                            {
                                plan = RoomsPlan();
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.GetValueOrDefault("queen", "0") == "1")
                            {
                                plan = QueenPlan();
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.GetValueOrDefault("celes", "0") == "1")
                            {
                                plan = CelesPlan();
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.GetValueOrDefault("clock", "0") == "1")
                            {
                                plan = ClockPlan();
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.GetValueOrDefault("morph", "0") == "1")
                            {
                                plan = MorphPlan();
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.GetValueOrDefault("atma", "0") == "1")
                            {
                                plan = AtmaPlan();
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.GetValueOrDefault("spells", "0") != "0")
                            {
                                plan = SpellsPlan();
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.GetValueOrDefault("features", "0") == "1")
                            {
                                plan = FeaturePlan();
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.GetValueOrDefault("battles", "0") == "1")
                            {
                                plan = BattlePlan();
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.GetValueOrDefault("tower", "0") == "1")
                            {
                                plan = TowerPlan();
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.GetValueOrDefault("objectives", "0") == "1")
                            {
                                plan = ObjectivePlan();
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            bool track = req.GetValueOrDefault("track", "0") == "1";
                            if (track && !req.ContainsKey("check"))
                            {
                                plan = TrackPlan();
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.TryGetValue("check", out var checkName))
                            {
                                plan = track ? TrackPlan() : new List<(float, Func<bool>)>();
                                foreach (var one in checkName.Split(','))
                                {
                                    var part = CheckPlans(one.Trim());
                                    if (part == null) { Check(false, $"no test plan for check {one}"); continue; }
                                    plan.AddRange(part);
                                }
                                if (plan.Count == 0) { Finish(); return; }
                                stepAt = now;
                                step = Step.Plan;
                                return;
                            }
                            if (req.GetValueOrDefault("tzen", "0") == "1")
                            {
                                Check(WcEvents.GotoMap(132, 1), "warp to Tzen (WoB)");
                                stepAt = now;
                                step = Step.Tzen;
                                return;
                            }
                            if (req.GetValueOrDefault("worlds", "0") == "2")
                            {
                                // the Falcon's wheel: scripts can't start right after flying, so from the
                                // Blackjack's deck (on foot): the Falcon placed and the WoR flag set as the
                                // Blackjack's wheel would, then a warp to the Falcon's deck
                                WcEvents.MoveAirship(4, 5, 2);
                                DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 97, 1);
                                Check(WcEvents.GotoMap(268, 1), "warp to the Falcon's deck");
                                stepAt = now;
                                step = Step.ToFalconDeck;
                                return;
                            }
                            Finish();
                        }
                        else if (now - stepAt > 120) { Check(false, $"New Game didn't reach the deck in 120 s: {report}; {WcEvents.LastStart}"); Finish(); }
                        return;
                    }
                case Step.ToWor:
                case Step.ToFalconDeck:
                case Step.ToWob:
                    {
                        var udm = UserDataManager.Instance();
                        var report = udm != null && Object.FindObjectOfType<FieldMap>() != null ? WcEvents.Report() : "not in the field";
                        if (report != lastReport) { Plugin.L.LogInfo($"[wc test]   {now - stepAt:0.0} s: {report}"); lastReport = report; }
                        int want = step == Step.ToWor ? 2 : step == Step.ToFalconDeck ? 268 : 1;
                        if (udm != null && udm.CurrentMapId == want && Object.FindObjectOfType<FieldMap>() != null && now - stepAt > 6)
                        {
                            if (step == Step.ToWor)
                            {
                                Check(report.Contains("Falcon map 2 at 84,34 enable True") && report.Contains("Falcon flag 1"),
                                      $"Blackjack wheel -> World of Ruin: {report}; {WcEvents.LastMove}");
                                // scripts can't start right after flying (the airship lands and boards
                                // itself): the Falcon's wheel is the worlds=2 run
                                Finish();
                                return;
                            }
                            else if (step == Step.ToFalconDeck)
                            {
                                Check(true, $"on the Falcon's deck: {report}");
                                WcEvents.RunScript("sc_e_FAoutboard", 1);   // (Fly to the World of Balance.)
                                step = Step.ToWob;
                            }
                            else
                            {
                                Check(report.Contains("Blackjack map 1 at 84,34 enable True") && report.Contains("Falcon flag 0"),
                                      $"Falcon wheel -> World of Balance: {report}; {WcEvents.LastMove}");
                                Finish();
                            }
                            stepAt = now;
                            return;
                        }
                        if (now - stepAt > 60) { Check(false, $"{step}: map {udm?.CurrentMapId} after 60 s, wanted {want}: {report}"); Finish(); }
                        return;
                    }
                case Step.Tzen:
                case Step.TzenBuy:
                case Step.TzenAgain:
                    {
                        var udm = UserDataManager.Instance();
                        var seed = WcSeeds.Active;
                        if (!seed.Checks.TryGetValue("tzen", out var tz)) { Check(false, "the seed has no Tzen check data"); Finish(); return; }
                        if (step == Step.Tzen)
                        {
                            if (udm.CurrentMapId != 132 || Object.FindObjectOfType<FieldMap>() == null || now - stepAt < 5)
                            {
                                if (now - stepAt > 60) { Check(false, $"didn't reach Tzen: map {udm.CurrentMapId}"); Finish(); }
                                return;
                            }
                            int price = tz.Prices.Length > 0 ? tz.Prices[0] : 0;
                            new OwnedItemClient().AddGil(price);   // enough to buy
                            gilBefore = udm.OwendGil;
                            itemsBefore = RewardCount(tz);
                            Plugin.L.LogInfo($"[wc test] Tzen: reward {string.Join(" ", tz.Rewards.Select(r => r.Kind + " " + r.Id))}, prices {string.Join("/", tz.Prices)}, gil {gilBefore}");
                            WcEvents.TestMessages.Clear();
                            Check(WcEvents.RunScript("sc_e_0427", 0), "talking to the seller");   // (Yes)
                            stepAt = now;
                            step = Step.TzenBuy;
                            return;
                        }
                        if (now - stepAt < 4) return;
                        if (step == Step.TzenBuy)
                        {
                            int price = tz.Prices.Length > 0 ? tz.Prices[0] : 0;
                            var offer = WcEvents.TestMessages.FirstOrDefault() ?? "";
                            Check(offer.Contains(price + " gil"), $"offer names the seed's price {price}: {offer}");
                            Check(udm.OwendGil == gilBefore - price, $"gil {gilBefore} -> {udm.OwendGil} (price {price})");
                            Check(RewardCount(tz) == itemsBefore + 1, $"reward received: {WcEvents.LastReward}; " +
                                  $"messages: {string.Join(" | ", WcEvents.TestMessages)}");
                            Check(WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 1) == 1 && WcEvents.Flag(DataStorage.Category.kScenarioFlag1, 206) == 1
                                  && WcEvents.Flag(DataStorage.Category.kScenarioFlag2, 57) == 1, "check flag + both bought flags set");
                            WcEvents.TestMessages.Clear();
                            gilBefore = udm.OwendGil;
                            WcEvents.RunScript("sc_e_0427", 0);
                            stepAt = now;
                            step = Step.TzenAgain;
                            return;
                        }
                        Check(udm.OwendGil == gilBefore && RewardCount(tz) == itemsBefore + 1,
                              $"talking again: no second sale ({string.Join(" | ", WcEvents.TestMessages)})");
                        Finish();
                        return;
                    }
                case Step.Plan:
                    {
                        if (plan.Count == 0) { Finish(); return; }
                        var (wait, act) = plan[0];
                        if (now - stepAt < wait) return;
                        if (WcEvents.Running && now - stepAt < wait + 90) return;   // let the scene finish
                        plan.RemoveAt(0);
                        stepAt = now;
                        try { if (!act()) { Finish(); return; } }
                        catch (Exception e) { Check(false, "plan step: " + e); Finish(); }
                        return;
                    }
                case Step.Battle:
                    {
                        // a battle with the test seed active: WC's enemy scaling (WcScaling)
                        if (now - stepAt < 4) return;
                        var controller = Object.FindObjectOfType<FieldMap>()?.fieldController;
                        var party = MasterManager.Instance.GetData<MonsterParty>(7);
                        if (controller == null || party == null) { Check(false, "no field controller for a test battle"); Finish(); return; }
                        Plugin.L.LogInfo("[wc test] starting formation 7 with the test seed active");
                        controller.Last_Map_IMapEncounter_ExcuteEncount(party, party.BattleBackgroundAssetId);
                        stepAt = now;
                        step = Step.InBattle;
                        return;
                    }
                case Step.InBattle:
                    {
                        var battle = BattlePlugManager.Instance();
                        if (battle != null && battle.IsBattle() && WcScaling.LastReport.StartsWith("formation "))
                        {
                            var e = battle.GetEnemyUnits()?.FirstOrDefault();
                            var p = e?.BattleUnitDataInfo?.Parameter;
                            var m = e?.GetMonster();
                            Check(p != null && m != null, $"scaling in battle: {WcScaling.LastReport}");
                            var autos = WcSeeds.Active?.Objectives.Where((o, i) => o.ResultName.StartsWith("Auto ") && WcObjectives.IsComplete(i)).Select(o => o.ResultName).ToList();
                            if (autos != null && autos.Count > 0)
                                Check(WcObjectives.LastAuto.Contains("on the party") && !WcObjectives.LastAuto.EndsWith("(0)"),
                                      $"{string.Join(", ", autos)}: {WcObjectives.LastAuto}");
                            if (p != null && m != null)
                                Check(p.ConfirmedLevel() == p.BaseLevel && p.CurrentHP == p.BaseMaxHp,
                                      $"monster {m.Id}: master Lv {m.Lv} HP {m.Hp} -> battle Lv {p.ConfirmedLevel()} HP {p.CurrentHP}/{p.ConfirmedMaxHp()}");
                            Finish();
                        }
                        else if (now - stepAt > 60) { Check(false, $"no scaled battle in 60 s (report: '{WcScaling.LastReport}')"); Finish(); }
                        return;
                    }
            }
        }

        // the title menu has our entry (id -1, text from our message key), and choosing it
        // (TitleWindowController.SetCommand, what the menu calls) opens the screen
        static void TitleMenuCheck()
        {
            try
            {
                string text = null;
                var seen = new List<string>();
                foreach (var ctrl in Resources.FindObjectsOfTypeAll<Last.UI.KeyInput.TitleMenuCommandController>())
                {
                    var views = ctrl?.contents;
                    int n = views?.Count ?? 0;
                    for (int i = 0; i < n; i++)
                    {
                        var v = views[i];
                        var t = v?.nameText?.text ?? "?";
                        if ((int)v.CommandId < 6) seen.Add($"{(int)v.CommandId}:{t}{(v.gameObject.activeInHierarchy ? "" : " (hidden)")}");
                        if ((int)v.CommandId == -1)
                        {
                            text = t;
                            var comps = v.nameText?.gameObject.GetComponents<Component>();
                            var names = new List<string>();
                            if (comps != null) foreach (var c in comps) names.Add(c.GetIl2CppType().FullName);
                            Plugin.L.LogInfo($"[wc test] our label's components: {string.Join(", ", names)}");
                        }
                    }
                }
                Check(text != null, $"title menu main entries [{string.Join(", ", seen)}]; ours {(text != null ? $"shows '{text}'" : "missing")}");
                LayoutDump();
                var title = Object.FindObjectOfType<Last.UI.KeyInput.TitleWindowController>();
                var set = typeof(Last.UI.KeyInput.TitleWindowController).GetMethod("SetCommand",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (title == null || set == null) { Check(false, "title menu: no title window / SetCommand"); return; }
                var ps = set.GetParameters();
                set.Invoke(title, new object[] { (Last.Defaine.TitleCommandId)(-1), Enum.ToObject(ps[1].ParameterType, 2), false, true });
                Check(WcScreen.Open, "title menu: choosing the entry opens the screen");
                var nav = WcScreen.NavTest();
                Check(nav == "B:list p0 Down:list p1 A:rows p1 RB:rows p2 B:list p2 B:closed list p2", "controller flow: " + nav);
                Check(true, "devices: " + ((Plugin.Features?.Get<SingleGamepad>()?.Scan() ?? "") is var devs && devs.Length > 0 ? devs : "none"));
                var pad = WcInput.PadCheck();
                Check(!pad.Contains("FAILED"), "input: " + pad);
                WcScreen.Show(false);
            }
            catch (Exception e) { Check(false, "title menu: " + (e.InnerException ?? e).Message); }
        }

        // where the title menu's entries sit and what lays them out (layout check, no screen):
        // the parents up to the canvas with their layout components, each main entry's rect,
        // and every other active text on that canvas
        internal static void LayoutDump()
        {
            try
            {
                Canvas.ForceUpdateCanvases();
                foreach (var ctrl in Resources.FindObjectsOfTypeAll<Last.UI.KeyInput.TitleMenuCommandController>())
                {
                    var views = ctrl?.contents;
                    if (views == null || views.Count == 0) continue;
                    var first = views[0]?.transform;
                    var parent = first?.parent;
                    if (parent == null) continue;
                    // the menu waits inactive behind "press any button": switch it on just for the
                    // layout pass (headless: nothing is drawn), then back
                    var menuGo = parent.parent?.gameObject;
                    bool wasActive = menuGo == null || menuGo.activeSelf;
                    if (!wasActive) menuGo.SetActive(true);
                    WcTitleMenu.FitForTest(ctrl);
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(parent.GetComponent<RectTransform>());
                    for (var t = parent; t != null; t = t.parent)
                    {
                        var rt = t.GetComponent<RectTransform>();
                        var comps = string.Join(",", t.GetComponents<Component>().Select(c => c.GetIl2CppType().Name));
                        string extra = "";
                        var vlg = t.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
                        if (vlg != null) extra = $" spacing {vlg.spacing} padding {vlg.padding.top}/{vlg.padding.bottom} childAlign {vlg.childAlignment} ctrlH {vlg.childControlHeight} forceH {vlg.childForceExpandHeight}";
                        var fit = t.GetComponent<UnityEngine.UI.ContentSizeFitter>();
                        if (fit != null) extra += $" fitter V {fit.verticalFit}";
                        Plugin.L.LogInfo($"[layout] parent '{t.name}' active {t.gameObject.activeSelf} [{comps}]" +
                                         (rt != null ? $" pos {rt.anchoredPosition} size {rt.sizeDelta} rect {rt.rect} pivot {rt.pivot} anchors {rt.anchorMin}-{rt.anchorMax}" : "") + extra);
                        if (t.GetComponent<Canvas>() != null && t.parent == null) break;
                    }
                    for (int i = 0; i < views.Count; i++)
                    {
                        var v = views[i];
                        if (v == null) continue;
                        var rt = v.GetComponent<RectTransform>();
                        var c = new Vector3[4];
                        rt?.GetWorldCorners(c);
                        Plugin.L.LogInfo($"[layout] entry {(int)v.CommandId} '{v.nameText?.text}' active {v.gameObject.activeSelf} sibling {v.transform.GetSiblingIndex()} " +
                                         $"pos {rt?.anchoredPosition} size {rt?.sizeDelta} world y {c[0].y:0}-{c[1].y:0} x {c[0].x:0}-{c[2].x:0}");
                    }
                    if (!wasActive) menuGo.SetActive(false);
                    var canvas = parent.GetComponentInParent<Canvas>();
                    var root = canvas != null ? canvas.rootCanvas.transform : parent.root;
                    foreach (var txt in root.GetComponentsInChildren<UnityEngine.UI.Text>(false))
                    {
                        if (txt == null || string.IsNullOrWhiteSpace(txt.text)) continue;
                        var c = new Vector3[4];
                        txt.rectTransform.GetWorldCorners(c);
                        var shown = txt.text.Replace('\n', ' ');
                        Plugin.L.LogInfo($"[layout] text '{shown}' on '{txt.transform.parent?.name}/{txt.name}' world y {c[0].y:0}-{c[1].y:0} x {c[0].x:0}-{c[2].x:0}");
                    }
                    break;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[layout] {e}"); }
        }

        // the seed list's Delete (WcScreen, WcSeeds.Delete; the user asked 2026-11-06 whether the
        // history grows forever): never the current seed or one a save uses; through the
        // screen's own rows (Right, A, A) the seed's folder goes and the list drops it
        static void DeleteChecks()
        {
            var first = made.Count > 1 ? WcSeeds.Load(made[0]) : null;
            var current = WcSeeds.Active;
            Check(first != null && current?.Id == made.Last(), $"two seeds made ({string.Join(", ", made)}), the second active ({current?.Id})");
            if (first == null || current == null) return;
            var err = WcSeeds.Delete(current);
            Check(err != null && Directory.Exists(current.Dir), $"the current seed isn't deleted: {err ?? "it was"}");
            WcSeeds.TestSlot(99, first.Id);
            try
            {
                err = WcSeeds.Delete(first);
                Check(err != null && err.Contains("slot 99") && Directory.Exists(first.Dir), $"a seed a save uses isn't deleted: {err ?? "it was"}");
            }
            finally { WcSeeds.TestSlot(99, null); }
            var steps = WcScreen.DeleteTest(first.Id);
            Check(steps.EndsWith("A: gone") && steps.Contains("Delete it?") && !Directory.Exists(first.Dir) && WcSeeds.All().All(s => s.Id != first.Id),
                  $"deleted from the seed list: {steps}");
            Check(WcSeeds.Active?.Id == current.Id && Directory.Exists(current.Dir), "the current seed stays");
        }

        static void Finish()
        {
            step = Step.Done;
            // leave the install as it was: previous seed (or none) active, test seeds deleted
            try
            {
                var prev = prevActive != null ? WcSeeds.Load(prevActive) : null;
                if (WcSeeds.Active?.Id != prev?.Id) WcSeeds.Apply(prev);
                foreach (var id in made)
                {
                    var dir = WcSeeds.DirFor(id);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
            }
            catch (Exception e) { Plugin.L.LogWarning($"[wc test] cleanup: {e.Message}"); }
            Plugin.L.LogInfo($"[wc test] done: {failures} failures; quitting");
            Application.Quit();
        }
    }
}
