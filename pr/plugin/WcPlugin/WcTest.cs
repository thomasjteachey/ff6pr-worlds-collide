using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ff6Mod.Shared;
using Last.Battle;
using Last.Data.Master;
using Last.Interpreter;
using Last.Management;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WorldsCollide.PR
{
    // Headless self-test (tools/wc_test_headless.ps1; no window, no sound): BepInEx/wc_test.request
    //   seed=..., preset=... or flags=..., seed2=... (a second seed: switching undoes the first),
    //   load=1 (then load the latest save through the hooked GotoLoadSaveData)
    //   delete=1 (with seed2: the seed list's Delete refuses the current seed and one a save
    //   uses, then deletes the first seed through the screen's rows)
    //   newgame=1 (after the seed: New Game from the title; WC's start must put the seed's party
    //   on the Blackjack's deck, airship outside Narshe; WcEvents)
    //   worlds=1 (with newgame: the Blackjack's wheel -> World of Ruin, the Falcon where the
    //   Blackjack was); worlds=2 (the Falcon's wheel -> World of Balance, from its deck)
    //   tzen=1 (with newgame: warp to Tzen, buy from the esper seller: the seed's price and
    //   reward, the check's flag; talking again gives nothing)
    //   check=<name> (with newgame: a check's own test plan, CheckPlans: warp, run its script,
    //   gate closed / open, reward and flags)
    // makes the seeds through the real generator on the title screen, applies them live, checks
    // live rows and Magicite files against the seed folders, puts the game's own data back,
    // deletes the test seeds and quits. Lines start with "[wc test]"; "[wc test] done" ends it.
    static class WcTest
    {
        static Dictionary<string, string> req;
        enum Step { Off, Startup, Wait, Gen1, Gen2, Vanilla, Load, Loading, Battle, InBattle, NewGame, InNewGame, ToWor, ToFalconDeck, ToWob, Tzen, TzenBuy, TzenAgain, Plan, Done }
        static Step step = Step.Off;
        internal static bool Running => step != Step.Off;   // until the game quits
        internal static bool NoAssetChange => req != null && req.GetValueOrDefault("noasset", "0") == "1";
        static float since = -1, stepAt;
        static readonly List<string> made = new List<string>();
        static int failures;
        static string prevActive;
        static string lastReport = "";
        static float deckAt = -1;
        static List<(float, Func<bool>)> plan;

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
            ["mobliz_wor"] = (207, "sc_e_0709_3"), ["doom_gaze"] = (268, "sc_e_0964_2"), ["magitek_factory_3"] = (403, "sc_e_0402"),
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
            int F4(int i) => WcEvents.Flag(DataStorage.Category.kScenarioFlag4, i);
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
                    var expect = party == null ? new List<int>() : new[] { party.Monster1, party.Monster2, party.Monster3, party.Monster4, party.Monster5, party.Monster6 }.Where(x => x > 0).ToList();
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
        // WC's (the SNES's) Regen/Sap/Poison ticks (WcStatusTicks): a test battle with the monsters
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
                var ts = WcStatusTicks.Log.Where(x => x.Slot == slot && x.Kind == kind && x.Time >= from && x.Time < to).Select(x => (double)x.Time).ToList();
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
                float now = WcStatusTicks.BattleTime;
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
                        var during = WcStatusTicks.Log.Where(x => x.Slot == 1 && x.Kind == "Poison" && x.Time >= jumpAt && x.Time < now).ToList();
                        Check(during.Count >= 1 && Has(party[1], 420), $"status_ticks: Poison ticks on while jumping ({during.Count} in {now - jumpAt:0.0} s, still jumping {Has(party[1], 420)})");
                        battle.BattleConditionController?.InterruptRemoveCondition(party[1], (Last.Defaine.ConditionType)420);
                        jumpEnd = now;
                    }
                    if (stopEnd < 0 && !Has(party[0], 17)) stopEnd = now;
                    if (stopEnd < 0 && now - stopAt > 90) { Check(false, "status_ticks: Stop still on after 90 s"); battle.BattleConditionController?.InterruptRemoveCondition(party[0], (Last.Defaine.ConditionType)17); stopEnd = now; }
                    if (stopEnd >= 0 && jumpEnd >= 0 && now - stopEnd >= Regen + 2.2)
                    {
                        var during = WcStatusTicks.Log.Where(x => x.Slot == 0 && x.Kind == "Regen" && x.Time > stopAt + 0.05f && x.Time < stopEnd).ToList();
                        var after = WcStatusTicks.Log.Where(x => x.Slot == 0 && x.Kind == "Regen" && x.Time >= stopEnd).ToList();
                        Check(during.Count == 0 && after.Count >= 1,
                              $"status_ticks: no Regen while stopped ({stopEnd - stopAt:0.0} s: {during.Count}), on again after ({after.Count}, first {(after.Count > 0 ? after[0].Time - stopEnd : -1):0.00} s on)");
                        Plugin.L.LogInfo($"[wc test] status_ticks: {WcStatusTicks.Ticks} ticks, {WcStatusTicks.Steps} steps; log " +
                                         string.Join(" ", WcStatusTicks.Log.Where(x => !x.Kind.EndsWith("-due")).Select(x => $"{x.Time:0.00}:{x.Slot}{x.Kind[0]}")));
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
                    Check(WcStatusTicks.Active, "status_ticks: the plugin's tick clock is on (a WC seed)");
                    savedMode = cfg.BattleModeIndex;
                    cfg.BattleModeIndex = 0;                                                   // Active
                    Hold();
                    BattleUtility.AddCondition(party[0], (Last.Defaine.ConditionType)25);      // Regen
                    BattleUtility.AddCondition(party[1], (Last.Defaine.ConditionType)10);      // Poison
                    BattleUtility.AddCondition(party[2], (Last.Defaine.ConditionType)407);     // Slip (Sap)
                    WcStatusTicks.Log.Clear();
                    t0 = WcStatusTicks.BattleTime;
                    Check(Has(party[0], 25) && Has(party[1], 10) && Has(party[2], 407), "status_ticks: Regen, Poison and Sap on the party");
                    return true;
                }),
            };
            for (int i = 0; i < 320; i++) steps.Add((1, Step));
            steps.Add((1, () => { Check(false, "status_ticks: didn't finish in 320 s"); return false; }));
            return steps;
        }

        // WC's (the SNES's) status timers and Jump (WcStatusTicks, WcAtb): a test battle, the monsters
        // stopped. (1) Sleep, Reflect, Freeze, Stop on the party's four: each off after 18 / 26 / 34 / 18
        // of the clock's steps (a step 2.13 s; the first comes 1-4 visits after the status is found), not
        // the PR's (Stop measured 31.3 s before). (2) Condemned on the first, as from a level-60 attacker:
        // the SNES's count (19-20 steps, C2/09B4) and the death at its end. (3) The second jumps on auto
        // (Jump dealt into its first slot: WcAutobattle's Fight-like default): its air time the SNES's (224/256 of a wait
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
            List<(float T, string K)> Of(int slot) => WcStatusTicks.Log.Where(x => x.Slot == slot).Select(x => (x.Time, x.Kind)).ToList();
            bool Step()
            {
                var battle = BattlePlugManager.Instance();
                if (battle == null || !battle.IsBattle()) { Check(false, "status_timers: the battle ended early"); return false; }
                Hold();
                float now = WcStatusTicks.BattleTime;
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
                        WcStatusTicks.SetDoomCaster(party[0], 60);
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
                    if (Dead(party[0]) || now - doomAt > 70)
                    {
                        double got = set.K == null ? -1 : now - set.T;
                        Check(Dead(party[0]) && doomLeft > 0 && got >= (doomLeft - 1) * StepS - 0.5 && got <= (doomLeft + 1) * StepS + 1.5,
                              $"status_timers: Condemned's end {got:0.0} s after its count was set ({doomLeft} steps: {doomLeft * StepS:0.0} s; dead {Dead(party[0])})");
                        // (3) Jump: the second jumps on auto, first with the PR's own air time
                        // Jump dealt into its first slot, no Fight (as WC deals it): auto-battle's default is
                        // the first slot's Jump (WcAutobattle's Fight-like commands), not the PR's Defend
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
                    if (pairs.Count >= 1 && !jumpChecked)
                    {
                        jumpChecked = true;
                        fightsFrom = now;
                        // the landing's chant is the SNES's air time (WcAtb.Chanting_Postfix); the PR's own:
                        // 600 / (Agility / 2 + 20) s at the default battle speed
                        double want = WcAtb.SnesJumpSeconds(speed, false, WcAtb.SnesBattleSpeed(), false, false);
                        double pr = 600.0 / (speed / 2.0 + 20);
                        Check(Math.Abs(pairs[0] - want) < 0.5 && WcAtb.JumpChants > 0,
                              $"status_timers: Jump's air time {pairs[0]:0.00} s (Speed {speed}: SNES {want:0.00} s, 1.75 gauges; the PR's own chant {pr:0.00} s; landings re-timed {WcAtb.JumpChants})");
                    }
                    if (jumpChecked)
                    {
                        // the command waits: the third's Fights on auto, first with the SNES's wait (C2/067B:
                        // 16, half the ATB's add a move), then with the game's own chant (WcAtb.CommandWaits
                        // off): its turns come the difference between the two closer together
                        var third = party.Length > 2 ? party[2] : null;
                        var tp = third?.BattleUnitDataInfo?.Parameter;
                        var fights = WcAtb.ChantLog.Where(x => third != null && x.Unit == third.Pointer && x.Command == 1).ToList();
                        if (WcAtb.CommandWaits)
                        {
                            if (fights.Count < 4 && now - fightsFrom < 60) return true;
                            WcAtb.CommandWaits = false;
                            fightsSnes = fights.Count;
                            fightsFrom = now;
                            return true;
                        }
                        if (fights.Count < fightsSnes + 4 && now - fightsFrom < 60) return true;
                        WcAtb.CommandWaits = true;
                        if (tp != null && fightsSnes >= 3 && fights.Count >= fightsSnes + 3)
                        {
                            int sp = tp.ConfirmedAgility();
                            double wait = WcAtb.SnesWaitSeconds(16, sp, false, 0, false, false), own = 60 / (sp / 2.0 + 20);
                            List<double> GapsFrom(int from, int to) => Enumerable.Range(from, Math.Max(0, to - from)).Where(i => i + 1 < fights.Count)
                                .Select(i => (double)(fights[i + 1].Time - fights[i].Time)).ToList();
                            var snesGaps = GapsFrom(1, fightsSnes - 1);           // (the first can hold the jump's turn)
                            var ownGaps = GapsFrom(fightsSnes, fights.Count - 1);
                            double diff = ownGaps.Average() - snesGaps.Average();
                            Check(Math.Abs(diff - (own - wait)) < 0.35 && fights.Take(fightsSnes).All(f => Math.Abs(f.Wait - wait) < 0.01),
                                  $"status_timers: Fight's wait {wait:0.00} s at Speed {sp} (SNES 16), the game's chant {own:0.00}: turns {string.Join(" ", snesGaps.Select(g => g.ToString("0.00")))} s apart with the SNES's, " +
                                  $"{string.Join(" ", ownGaps.Select(g => g.ToString("0.00")))} with the game's: {diff:0.00} s more (want {own - wait:0.00})");
                        }
                        else Check(false, $"status_timers: the third's Fights on auto ({fightsSnes} with the SNES's wait, {fights.Count - fightsSnes} with the game's)");
                        Last.UI.BattleUIManager.Instance?.SetIsAuto(false);
                        RestoreJumpRow();
                        Plugin.L.LogInfo($"[wc test] status_timers: {WcStatusTicks.Removed} statuses taken off; log " +
                                         string.Join(" ", WcStatusTicks.Log.Where(x => !x.Kind.EndsWith("-due")).Select(x => $"{x.Time:0.00}:{x.Slot}{x.Kind}")));
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
                    Check(WcStatusTicks.Active, "status_timers: the plugin's status clock is on (a WC seed)");
                    savedMode = cfg.BattleModeIndex;
                    cfg.BattleModeIndex = 0;                                                   // Active
                    Hold();
                    var add = new[] { (7, "Sleep", 18), (413, "Reflect", 26), (603, "Freeze", 34), (17, "Stop", 18) };
                    for (int i = 0; i < add.Length && i < party.Length; i++)
                    {
                        BattleUtility.AddCondition(party[i], (Last.Defaine.ConditionType)add[i].Item1);
                        wantOff[i] = (add[i].Item2, add[i].Item3);
                    }
                    WcStatusTicks.Log.Clear();
                    t0 = WcStatusTicks.BattleTime;
                    Plugin.L.LogInfo($"[wc test] status_timers: Doom pauses on [{string.Join(",", DoomConditionFunction.StopConditions?.ToArray().Select(c => (int)c) ?? new int[0])}]");
                    return true;
                }),
            };
            for (int i = 0; i < 500; i++) steps.Add((1, Step));
            steps.Add((1, () => { Check(false, "status_timers: didn't finish in 500 s"); RestoreJumpRow(); WcAtb.CommandWaits = true; return false; }));
            return steps;
        }

        // Where a character's turn goes (the user, 2026-11-12: "look into" the ~2.1 s a turn beyond gauge +
        // wait): a test battle, monsters stopped and kept alive, Active mode, auto-battle on; the first
        // party member's gauge, charge (IsChantTimeActDataByUnit) and act (BattleActExection's staging
        // act and state) recorded every frame with the real time and the battle's (WcStatusTicks). Each
        // turn split into: the fill (0 -> full), full -> its charge starts (the auto-battle's pick), the
        // charge, charge end -> its act starts, the act, the act's end -> the gauge rising again; each in
        // the battle's time (the PR's runs only outside acts) and real time. A record, not a pass/fail.
        // The SNES's (harness, Speed 33, battle-time ticks): fill 206 ticks (6.86 s), Fight's wait 26
        // (0.87 s), its attack 37 ticks over 86 frames (1.43 s real) with time running, the refill from its end.
        static List<(float, Func<bool>)> TurnCyclePlan()
        {
            int savedMode = -1;
            BattlePlayerData me = null;
            var rec = new List<(int F, float Real, float Bt, float Gauge, bool Chant, bool Acting, bool Mine, int State)>();
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
                rec.Add((f, Time.realtimeSinceStartup, WcStatusTicks.BattleTime, prog.GetAtbGaugeByUnitData(me),
                         prog.IsChantTimeActDataByUnit(me), acting, mine, exe == null ? -1 : (int)exe.nowState));
            }
            var turns = new List<(double Pick, double Charge, double Restart, double Fill, double Scale, double ActScale, double ActReal)>();
            string Analyse()
            {
                var lines = new List<string>();
                turns.Clear();
                int i = 0;
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
                    string Bt(int a, int b) => a < 0 || b < 0 ? "?" : $"{rec[b].Bt - rec[a].Bt:0.00}";
                    string Re(int a, int b) => a < 0 || b < 0 ? "?" : $"{rec[b].Real - rec[a].Real:0.00}";
                    int chantEnd = chant < 0 ? -1 : rec.FindIndex(chant, r => !r.Chant);
                    var states = string.Join(",", rec.Skip(act).Take(Math.Max(0, actEnd - act)).Select(r => r.State).Distinct());
                    if (chant >= 0 && chantEnd >= 0 && rise >= 0 && next >= 0)
                    {
                        double scale = (rec[next].Bt - rec[full].Bt) / Math.Max(1e-3, rec[next].Real - rec[full].Real);
                        double actScale = (rec[actEnd].Bt - rec[act].Bt) / Math.Max(1e-3, rec[actEnd].Real - rec[act].Real);
                        turns.Add((rec[chant].Bt - rec[full].Bt, rec[chantEnd].Bt - rec[chant].Bt, rec[rise].Bt - rec[actEnd].Bt,
                                   rec[next].Bt - rec[rise].Bt, scale, actScale, rec[actEnd].Real - rec[act].Real));
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
                    WcStatusTicks.FrameHook = Record;
                    t0 = WcStatusTicks.BattleTime;
                    var p = me.BattleUnitDataInfo.Parameter;
                    Plugin.L.LogInfo($"[wc test] turn_cycle: recording {me.GetOwnedCharacterData()?.characterStatusId} (Agility {p.ConfirmedAgility()}: " +
                                     $"a gauge {100 / WcAtb.SnesRate(p.ConfirmedAgility(), false, 0, false, false):0.00} s, Fight's wait {WcAtb.SnesWaitSeconds(16, p.ConfirmedAgility(), false, 0, false, false):0.00} s)");
                    return true;
                }),
                (15, () => { Hold(); return true; }), (15, () => { Hold(); return true; }), (15, () => { Hold(); return true; }), (15, () => { Hold(); return true; }),
                (5, () =>
                {
                    WcStatusTicks.FrameHook = null;
                    Last.UI.BattleUIManager.Instance?.SetIsAuto(false);
                    var cfg = UserDataManager.Instance()?.Config;
                    if (cfg != null && savedMode >= 0) cfg.BattleModeIndex = savedMode;
                    string a = Analyse();
                    Plugin.L.LogInfo($"[wc test] turn_cycle: {rec.Count} frames, battle time {WcStatusTicks.BattleTime - t0:0.0} s\n{a}");
                    var mp = me.BattleUnitDataInfo.Parameter;
                    int sp = mp.ConfirmedAgility();
                    double gauge = 100 / WcAtb.SnesRate(sp, false, 0, false, false), wait = WcAtb.SnesWaitSeconds(16, sp, false, 0, false, false);
                    Check(turns.Count >= 3, $"turn_cycle: {turns.Count} whole turns recorded");
                    if (turns.Count > 0)
                    {
                        Check(turns.All(t => t.Pick < 0.1 && t.Restart < 0.1),
                              $"turn_cycle: the auto-battle's pick and the gauge's restart take no time (picks {string.Join(" ", turns.Select(t => t.Pick.ToString("0.00")))}, restarts {string.Join(" ", turns.Select(t => t.Restart.ToString("0.00")))})");
                        Check(turns.All(t => Math.Abs(t.Charge - wait) < 0.06 && Math.Abs(t.Fill - gauge) < 0.12),
                              $"turn_cycle: each charge the SNES's Fight wait {wait:0.00} s ({string.Join(" ", turns.Select(t => t.Charge.ToString("0.00")))}), each fill a SNES gauge {gauge:0.00} s ({string.Join(" ", turns.Select(t => t.Fill.ToString("0.00")))})");
                        Check(turns.All(t => Math.Abs(t.ActScale - t.Scale) < 0.08),
                              $"turn_cycle: the battle's time runs through the attack, as on the SNES (battle/real time over a turn {string.Join(" ", turns.Select(t => t.Scale.ToString("0.00")))}, " +
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
                int want = recruited.Sum(c => c.Parameter.BaseLevel) / Math.Max(1, recruited.Count);
                if (newcomer > 0)
                {
                    WcEvents.Join(newcomer);
                    Last.Data.User.OwnedCharacterData joined = null;
                    foreach (var c in udm.OwnedCharacterList) if (c?.characterStatusId == newcomer) joined = c;
                    Check(joined != null && joined.Parameter.BaseLevel == want,
                          $"-sal: character {newcomer} joined at level {joined?.Parameter.BaseLevel} (average {want}); {WcRecruits.LastJoin}");
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
                // -eu: Umaro's Equip menu
                bool open = false;
                WcRecruits.CanOpen_Postfix(Last.Defaine.MenuCommandId.Equipment, 14, ref open);
                Check(open == s.Flags.Split(' ').Contains("-eu"), $"-eu: Umaro may open Equip: {open}");
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
                // -chrm: a monster box's battle, queued as opening the chest does, starts on its own
                boxParty = s.MonsterChests.Values.First();
                WcBattles.Queue(boxParty);
                return true;
            };
            Func<bool> box = () =>
            {
                var b = BattlePlugManager.Instance();
                var party = MasterManager.Instance.GetData<MonsterParty>(boxParty);
                var expect = party == null ? new List<int>() : new[] { party.Monster1, party.Monster2, party.Monster3, party.Monster4, party.Monster5, party.Monster6 }.Where(x => x > 0).ToList();
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
                    Check(!WcTrack.Pick_Prefix((Last.Defaine.MenuCommandId)100) && WcScreen.Open && WcScreen.Track, "picking Track opens the Track pages");
                    var pages = WcScreen.TrackTest();
                    Check(pages.StartsWith("Checks:") && !pages.Contains(":0 ") && !pages.Contains(":1 "), "track pages: " + pages);
                    Check(WcTrack.Pick_Prefix((Last.Defaine.MenuCommandId)7), "other entries go to the game");
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

        static List<(float, Func<bool>)> CheckPlans(string name)
        {
            if (name == "empty_chest")
            {
                // a chest the PR keeps empty that the seed filled (port_chests.fill_empty): South
                // Figaro's basement room (map 52); opened, its script gives the seed's item and line
                const string Rel = "map_20071/Assets/GameAssets/Serial/Res/Map/Map_20071/Map_20071_14/sc_e_no_treasure_32.json";
                int item = -1, num = 0;
                try
                {
                    var path = Path.Combine(WcSeeds.Active?.Dir ?? "", "files", Rel);
                    if (File.Exists(path))
                        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
                            foreach (var m in doc.RootElement.GetProperty("Mnemonics").EnumerateArray())
                            {
                                if (m.GetProperty("mnemonic").GetString() != "SysCall") continue;
                                var call = m.GetProperty("operands").GetProperty("sValues")[0].GetString().Split(' ');
                                if (call[0] != "WC:item") continue;
                                item = int.Parse(call[1]);
                                num = int.Parse(call[2]);
                                break;
                            }
                }
                catch (Exception e) { Check(false, $"empty_chest: the seed's script: {e.Message}"); }
                int Count()   // every kind (equipment too: SearchOwnedItem finds items only)
                {
                    int n = 0;
                    var all = item < 0 ? null : UserDataManager.Instance()?.GetAllOwnedItemsClone();
                    if (all != null) foreach (var it in all) if (it != null && it.ContentId == item) n += it.Count;
                    return n;
                }
                int had = 0;
                return new List<(float, Func<bool>)>
                {
                    (0, () => { Check(WcEvents.GotoMap(52, 1), "warp to South Figaro's basement room (map 52)"); return true; }),
                    (3, () =>
                    {
                        had = Count();
                        WcEvents.TestMessages.Clear();
                        Check(item >= 0, $"empty_chest: the seed fills it (item {item} x{num})");
                        if (item >= 0) Check(WcEvents.RunScript("sc_e_no_treasure_32"), "empty_chest: its script ran");
                        return true;
                    }),
                    (4, () =>
                    {
                        if (item >= 0)
                            Check(Count() == had + num && WcEvents.TestMessages.Any(m => m.StartsWith("WC_REWARD: <CT>Obtained")),
                                  $"empty_chest: item {item} {had} -> {Count()}; lines {string.Join(" | ", WcEvents.TestMessages)}");
                        return true;
                    }),
                };
            }
            if (name == "train_chest")
            {
                // the Phantom Train's rack (map 366): the PR's Tent pick-up stands for SNES chest 109,
                // and chest 110 beside it (the PR dropped it) joins it (port_chests.MERGED): picked
                // up, it gives both of the seed's items, each with its line
                const string Rel = "map_30441/Assets/GameAssets/Serial/Res/Map/Map_30441/Map_30441_10/sc_e_1107.json";
                var gifts = new List<(int Item, int Num)>();
                try
                {
                    var path = Path.Combine(WcSeeds.Active?.Dir ?? "", "files", Rel);
                    if (File.Exists(path))
                        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
                            foreach (var m in doc.RootElement.GetProperty("Mnemonics").EnumerateArray())
                            {
                                if (m.GetProperty("mnemonic").GetString() != "SysCall") continue;
                                var call = m.GetProperty("operands").GetProperty("sValues")[0].GetString().Split(' ');
                                if (call[0] == "WC:item") gifts.Add((int.Parse(call[1]), int.Parse(call[2])));
                            }
                }
                catch (Exception e) { Check(false, $"train_chest: the seed's script: {e.Message}"); }
                int Count(int item)
                {
                    int n = 0;
                    var all = UserDataManager.Instance()?.GetAllOwnedItemsClone();
                    if (all != null) foreach (var it in all) if (it != null && it.ContentId == item) n += it.Count;
                    return n;
                }
                var had = new Dictionary<int, int>();
                return new List<(float, Func<bool>)>
                {
                    (0, () => { Check(WcEvents.GotoMap(366, 4), "warp to the Phantom Train's rack car (map 366)"); return true; }),
                    (3, () =>
                    {
                        foreach (var g in gifts) had[g.Item] = Count(g.Item);
                        WcEvents.TestMessages.Clear();
                        // two in a seed that fills both rack chests (WC's shuffle may leave one empty)
                        Check(gifts.Count >= 1, $"train_chest: the pick-up gives {gifts.Count} of the seed's items ({string.Join(", ", gifts.Select(g => $"{g.Item} x{g.Num}"))})");
                        if (gifts.Count > 0) Check(WcEvents.RunScript("sc_e_1107"), "train_chest: its script ran");
                        return true;
                    }),
                    (4, () =>
                    {
                        var want = gifts.GroupBy(g => g.Item).ToDictionary(x => x.Key, x => x.Sum(g => g.Num));
                        bool got = want.All(kv => Count(kv.Key) == had[kv.Key] + kv.Value);
                        int lines = WcEvents.TestMessages.Count(m => m.StartsWith("WC_REWARD: <CT>Obtained"));
                        Check(got && lines == gifts.Count,
                              $"train_chest: {string.Join(", ", want.Select(kv => $"item {kv.Key} {had[kv.Key]} -> {Count(kv.Key)}"))}; lines {string.Join(" | ", WcEvents.TestMessages)}");
                        return true;
                    }),
                };
            }
            if (name == "monster_box")
            {
                // a monster-in-a-box the seed changed (-cms moves packs between boxes, -fer changes
                // packs: both presets): opened, its battle is the one the port gave its script (the
                // plugin's event remap leaves box scripts alone), built on the box's own background
                // (port_chests.box_look), not the party's home one
                var boxes = new (string Script, string Folder, int Map, int Point, int Own)[]
                {
                    ("sc_e_0021", "map_20011/Assets/GameAssets/Serial/Res/Map/Map_20011/Map_20011_2", 5, 2, 516),
                    ("sc_e_0698", "map_30651/Assets/GameAssets/Serial/Res/Map/Map_30651/Map_30651_10", 465, 1, 434),
                    ("sc_e_0742", "map_30761/Assets/GameAssets/Serial/Res/Map/Map_30761/Map_30761_2", 489, 1, 127),
                    ("sc_e_0786", "map_30771/Assets/GameAssets/Serial/Res/Map/Map_30771/Map_30771_3", 495, 2, 236),
                    ("sc_e_0641", "map_30611/Assets/GameAssets/Serial/Res/Map/Map_30611/Map_30611_1", 433, 1, 212),
                    ("sc_e_0642", "map_30611/Assets/GameAssets/Serial/Res/Map/Map_30611/Map_30611_2", 434, 2, 212),
                };
                int Fights(string path)
                {
                    if (!File.Exists(path)) return -1;
                    using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
                        foreach (var m in doc.RootElement.GetProperty("Mnemonics").EnumerateArray())
                        {
                            var mn = m.GetProperty("mnemonic").GetString();
                            if (mn == "Encount" || mn == "EncountBoss") return m.GetProperty("operands").GetProperty("iValues")[0].GetInt32();
                        }
                    return -1;
                }
                int Background(int party) => MasterManager.Instance.GetData<MonsterParty>(party)?.BattleBackgroundAssetId ?? -1;
                (string Script, string Folder, int Map, int Point, int Own) box = default;
                int want = -1;
                foreach (var b in boxes)
                {
                    int p = -1;
                    try { p = Fights(Path.Combine(WcSeeds.Active?.Dir ?? "", "files", b.Folder, b.Script + ".json")); }
                    catch (Exception e) { Check(false, $"monster_box: {b.Script}'s seed script: {e.Message}"); }
                    if (p < 0 || p == b.Own) continue;
                    // a box fighting a copy on its own background first (the port's parties from 2000)
                    if (want < 0 || (want < 2000 && p >= 2000)) { box = b; want = p; }
                }
                return new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        Check(want > 0, $"monster_box: the seed changed a box's battle ({box.Script}: party {want}, vanilla {box.Own})");
                        if (want <= 0) return false;
                        Check(WcEvents.GotoMap(box.Map, box.Point), $"warp to {box.Script}'s map ({box.Map})");
                        return true;
                    }),
                    (4, () =>
                    {
                        WcBattles.BuiltParty = WcBattles.BuiltBackground = WcBattles.PartyBackground = -1;
                        Check(WcEvents.RunScript(box.Script), $"monster_box: {box.Script} ran (the box opened)");
                        return true;
                    }),
                    (12, () =>
                    {
                        var b = BattlePlugManager.Instance();
                        var ids = b?.GetEnemyUnits()?.Select(e => e?.GetMonster()?.Id ?? 0).Where(i => i > 0).ToList() ?? new List<int>();
                        var expect = WcBattles.Members(want);
                        Check(b != null && b.IsBattle() && WcBattles.BuiltParty == want && ids.Count > 0 && ids.All(expect.Contains),
                              $"monster_box: the battle is party {WcBattles.BuiltParty}, monsters [{string.Join(" ", ids)}] (the seed's {want}: [{string.Join(" ", expect)}])");
                        // a script's Encount builds the battle with background 0: the party's then
                        int shown = WcBattles.BuiltBackground > 0 ? WcBattles.BuiltBackground : WcBattles.PartyBackground;
                        Check(shown == Background(box.Own),
                              $"monster_box: on background {shown} (built with {WcBattles.BuiltBackground}, the party's {WcBattles.PartyBackground}), the box's {Background(box.Own)}");
                        return false;
                    }),
                };
            }
            if (name == "veldt")
            {
                // the Veldt opens a fight when its party is met elsewhere; a WC seed's encounters are
                // per-area copies of a formation's party (2000+): meeting one opens its formation's
                // Veldt fight (WcVeldt; the user's seed had a Veldt with nothing to fight)
                return new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        var be = global::Last.Map.Encount.BeastFieldEncount.Instance();
                        var table = be?.targetGroupPartyIdTable;
                        Check(be != null && table != null && table.Count > 0, $"veldt: the Veldt's table ({table?.Count ?? 0} parties)");
                        if (be == null || table == null || be.openFlags == null) return false;
                        int copy = -1, src = -1, flag = -1, copies = 0;
                        foreach (var kv in MasterManager.Instance.GetList<MonsterParty>())
                        {
                            if (WcVeldt.Formation(kv.Key) < 0) continue;
                            copies++;
                            int s = WcVeldt.Source(kv.Key);
                            if (copy > 0 || !table.ContainsKey(s)) continue;
                            int f = table[s].PartyTable.flag_index;
                            if (f < 0 || f >= be.openFlags.Length || be.openFlags[f]) continue;   // open already
                            copy = kv.Key; src = s; flag = f;
                        }
                        Check(copy > 0, $"veldt: of the seed's {copies} copied parties, {copy} copies {src}, a closed Veldt fight (flag {flag})");
                        if (copy < 0) return false;
                        be.EncounterMonsterParty(copy);
                        Check(be.openFlags[flag], $"veldt: meeting party {copy} opened the Veldt fight of {src} (flag {flag}; {WcVeldt.Mapped})");
                        return false;
                    }),
                };
            }
            if (name == "veldt_reward")
            {
                // the dried meat's first-join branch (the Veldt battle's "ガウ初回予約", which the user's
                // seed took, 2026-11-11): the reward in the battle, no scene reserved (vanilla reserves
                // sc_e_0239: the Blackjack parked at (1, 1), Gau's scene map, the Crescent Mountain
                // cave)
                var reserve = HarmonyLib.AccessTools.Method(typeof(Last.Interpreter.Instructions.SystemCall.Current), "ReserveScriptGauFirstJoin");
                bool InParty(int status) => UserDataManager.Instance()?.GetPartyMemberData()?.ToArray().Any(c => c?.characterStatusId == status) == true;
                return new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        if (!InParty(12)) WcEvents.Join(12);
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, 31, 0);   // the check open
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag1, 126, 0);
                        Last.Interpreter.Integrator.Instance?.CancelReservedScript();
                        Check(reserve != null && InParty(12) && WcEvents.VeldtRewardEncounter(),
                              $"veldt_reward: Gau in the party ({InParty(12)}), the check open: the reward encounter");
                        int before = WcEvents.GauFirstJoins;
                        reserve?.Invoke(null, new object[] { new Last.Interpreter.MainCore() });
                        var integ = Last.Interpreter.Integrator.Instance;
                        Check(WcEvents.GauFirstJoins == before + 1, "veldt_reward: the first-join reservation reached the plugin");
                        Check(WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 31) == 1 && (WcEvents.LastReward ?? "").StartsWith("veldt"),
                              $"veldt_reward: the reward given in the battle ({WcEvents.LastReward})");
                        Check(integ == null || !integ.ExistsReserveScript(),
                              $"veldt_reward: no script reserved ({(integ != null && integ.ExistsReserveScript() ? integ.reservedScript : "none")})");
                        Check(WcEvents.Flag(DataStorage.Category.kScenarioFlag4, WcEvents.PickParty) == 0, "veldt_reward: no party pick pending");
                        Check(WcEvents.Flag(DataStorage.Category.kScenarioFlag1, 126) == 1, "veldt_reward: Gau met at the Veldt (ScenarioFlag1 126)");
                        // its line waits for the battle's notice window (the battle script shows it next)
                        var text = MessageManager.Instance?.GetMessage("WC_REWARD", false) ?? "";
                        Check(WcEvents.Flag(DataStorage.Category.kScenarioFlag4, WcEvents.RewardLine) == 1 && text.Length > 0 && !text.Contains("<CT>"),
                              $"veldt_reward: the line waits for the battle window ('{text}')");
                        // (a Gau back from a Leap, out of the party: the game's own rejoin. Not
                        // reproduced here: turning his party entry off leaves him in the party)
                        return false;
                    }),
                };
            }
            if (name == "doma_play")
            {
                // Doma's siege played through for a side-by-side with WC on the SNES (the user, 2026-11-11:
                // "play through the doma siege event on both the WC snes and pr WC port and tell me of
                // any behavior/number/timing discrepancies"): into Doma (map 75) from its entrance with
                // Cyan recruited, the siege not on and the check open; a per-frame record (map, party cell,
                // a scene running, the screen black, messages, battle, flags, espers) as "[doma] t ..."
                // lines; once the party is free, the captain talked to (the game's own NPC action), the
                // battle on auto, then the reward and where the party ends up. Messages don't wait for a
                // button here (no input headless), so scene times are without reading time.
                float t0 = 0, freeAt = -1, battleAt = -1, endAt = -1, settledAt = -1;
                var seen = new Dictionary<string, string>();
                int msgs = 0;
                void Note(string k, string v)
                {
                    if (seen.TryGetValue(k, out var old) && old == v) return;
                    seen[k] = v;
                    Plugin.L.LogInfo($"[doma] {Time.realtimeSinceStartup - t0:0.00} s  {k} {v}");
                }
                void Record()
                {
                    var udm = UserDataManager.Instance();
                    var battle = BattlePlugManager.Instance();
                    bool inBattle = battle != null && battle.IsBattle();
                    Note("map", (udm?.CurrentMapId ?? -1).ToString());
                    if (!inBattle)
                    {
                        string cell = "-";
                        try { cell = WcEvents.PlayerCell()?.ToString() ?? "-"; } catch (Exception) { }   // mid map change
                        Note("cell", cell);
                    }
                    bool busy = WcEvents.EventBusy();
                    Note("scene", busy ? $"running {Last.Interpreter.Integrator.Instance?.scriptName}" : "idle");
                    var (black, _) = ScreenBlack();
                    Note("screen", black ? "black" : "shown");
                    Note("battle", inBattle ? $"party {battle.MonsterParty?.Id}" : "none");
                    if (inBattle) Note("monsters", WcScaling.LastReport);
                    Note("flags", $"siege {WcEvents.Flag(DataStorage.Category.kScenarioFlag1, 72)} scene {WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 140)} check {WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 57)}");
                    Note("espers", (udm?.OwnedMagicStoneList?.Count ?? 0).ToString());
                    while (msgs < WcEvents.TestMessages.Count) Plugin.L.LogInfo($"[doma] {Time.realtimeSinceStartup - t0:0.00} s  message   {WcEvents.TestMessages[msgs++]}");
                    // free: the siege scene has run (ScenarioFlag4 140) and nothing runs, the screen up, for a second
                    bool settled = !busy && !black && (udm?.CurrentMapId ?? -1) == 75 && WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 140) == 1;
                    if (!settled) settledAt = -1;
                    else if (settledAt < 0) settledAt = Time.realtimeSinceStartup;
                    if (freeAt < 0 && settledAt > 0 && Time.realtimeSinceStartup - settledAt > 1) freeAt = settledAt;
                    if (battleAt < 0 && inBattle) battleAt = Time.realtimeSinceStartup;
                }
                Last.Entity.Field.FieldEntity Npc(int script)
                {
                    var fc = Field();
                    for (int id = 0; id < 256; id++)
                    {
                        Last.Entity.Field.FieldEntity e = null;
                        try { e = fc?.GetFieldEntity(id); } catch (Exception) { }
                        int sid = -1;
                        try { sid = e?.Property?.TryCast<Last.Map.PropertyEvent>()?.ScriptId ?? -1; } catch (Exception) { }
                        if (sid == script) return e;
                    }
                    return null;
                }
                var steps = new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        if (!WcEvents.IsRecruited(3)) WcEvents.Join(3);
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag1, 72, 0);    // the siege not on
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, 57, 0);    // the check open
                        Plugin.L.LogInfo($"[doma] party {string.Join(" ", UserDataManager.Instance().GetPartyMemberData().ToArray().Select(c => $"{c.characterStatusId}:Lv{c.Parameter.ConfirmedLevel()}"))}");
                        // the World of Balance's point outside Doma's gate (157,86), below its exits (156/157,85)
                        Check(WcEvents.GotoMap(WcEvents.WobMap, 36), "doma_play: onto the World of Balance map outside Doma");
                        return true;
                    }),
                    (6, () =>
                    {
                        // north onto Doma's world map exit, as a player walks in
                        WcEvents.TestMessages.Clear();
                        t0 = Time.realtimeSinceStartup;
                        recorder = Record;
                        Plugin.L.LogInfo($"[doma] at {WcEvents.PlayerCell()} on map {UserDataManager.Instance()?.CurrentMapId}: walking north into Doma");
                        Walk(0, 1, 3);
                        return true;
                    }),
                };
                // the party free on the siege map: talk to the captain (sc_e_0110, script 139)
                for (int k = 0; k < 12; k++)
                {
                    steps.Add((2.5f, () =>
                    {
                        if (freeAt < 0) return true;
                        if (battleAt >= 0) return true;
                        var cap = Npc(139);
                        var cell = WcEvents.PlayerCell();
                        Plugin.L.LogInfo($"[doma] {Time.realtimeSinceStartup - t0:0.00} s  free at {freeAt - t0:0.00} s; party at {cell}; captain {(cap == null ? "not found" : cap.transform.position.ToString())}; soldiers {string.Join(" ", Enumerable.Range(131, 8).Select(s => Npc(s) != null ? "y" : "n"))}");
                        var ep = Object.FindObjectOfType<MainGame>()?.eventProcedure;
                        if (cap == null || ep == null) { Check(false, "doma_play: the captain to talk to"); return false; }
                        var act = ep.RequestEntityAction(cap);
                        WcEvents.Running = act != null;
                        Plugin.L.LogInfo($"[doma] {Time.realtimeSinceStartup - t0:0.00} s  talked to the captain: {(act == null ? "no action" : "his script")}");
                        battleAt = 0;   // asked: the battle comes next
                        return true;
                    }));
                }
                // the battle on auto until it ends, then what follows (up to 3 minutes)
                for (int k = 0; k < 36; k++)
                {
                    int round = k;
                    steps.Add((5f, () =>
                    {
                        var battle = BattlePlugManager.Instance();
                        bool inBattle = battle != null && battle.IsBattle();
                        if (inBattle)
                        {
                            var ui = Last.UI.BattleUIManager.Instance;
                            if (ui != null && !ui.IsAuto()) ui.SetIsAuto(true);
                            return true;
                        }
                        int check = WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 57);
                        if (check == 1 && !WcEvents.EventBusy() && !ScreenBlack().Item1)
                        {
                            if (endAt < 0) endAt = Time.realtimeSinceStartup;
                            if (Time.realtimeSinceStartup - endAt < 5) return true;
                            recorder = null;
                            Check(true, $"doma_play: done at {endAt - t0:0.00} s (free at {freeAt - t0:0.00} s); party at {WcEvents.PlayerCell()} on map {UserDataManager.Instance()?.CurrentMapId}");
                            return false;
                        }
                        if (round == 35) { recorder = null; Check(false, $"doma_play: no end in 3 minutes (check flag {check}, scene {Last.Interpreter.Integrator.Instance?.scriptName})"); return false; }
                        return true;
                    }));
                }
                return steps;
            }
if (name == "doma_start")
            {
                // walking into Doma (map 75) in the WoB with Cyan, the siege not on, the check open:
                // the siege scene (sc_e_0099_3) runs to its end and fades in. The user's Deck,
                // 2026-11-11: entering froze on the last frame (the map script had started the scene
                // with ChangeScript while the map was loading; the scene calls that map script itself)
                return new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        if (!WcEvents.IsRecruited(3)) WcEvents.Join(3);
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag1, 72, 0);    // the siege not on
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag4, 57, 0);    // the check open
                        Check(WcEvents.GotoMap(75, 1), "doma_start: into Doma (map 75)");
                        return true;
                    }),
                    (40, () =>
                    {
                        var (black, why) = ScreenBlack();
                        bool busy = WcEvents.EventBusy();
                        Check(WcEvents.Flag(DataStorage.Category.kScenarioFlag1, 72) == 1, "doma_start: the siege is on (ScenarioFlag1 72)");
                        Check(WcEvents.Flag(DataStorage.Category.kScenarioFlag4, 140) == 1, "doma_start: the siege scene ran (ScenarioFlag4 140)");
                        Check(!busy, $"doma_start: no scene left running ({Last.Interpreter.Integrator.Instance?.scriptName})");
                        Check(!black, $"doma_start: the screen faded in ({why})");
                        Check((UserDataManager.Instance()?.CurrentMapId ?? -1) == 75, $"doma_start: on Doma's map ({UserDataManager.Instance()?.CurrentMapId})");
                        return false;
                    }),
                };
            }
            if (name == "encounters")
            {
                // WC's (the SNES's) random battles (WcEncounters): on the World of Balance south of
                // Narshe (zone 10), the cell's SNES rate, and the hook's danger counter over 3000 steps
                // at that rate: the mean steps per battle against the SNES's (exact means from
                // tests/test_wc.py's model); then walking on with the hook live: the game counts the
                // steps through it and a battle comes from a step. (The game's CheckEncount can't be
                // called from managed code: its "in List" parameter's type doesn't load.)
                var means = new Dictionary<int, double>
                {
                    [0xC0] = 23.35, [0x60] = 33.61, [0x180] = 16.22,                 // world: normal, low, high
                    [0x70] = 31.1, [0x40] = 41.3, [0x160] = 17.09, [0x200] = 13.86, // other maps
                };
                int battlesBefore = 0, stepsBefore = 0, setUpBefore = 0;
                string scaledBefore = "";
                var steps = new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        WcEncounters.Paused = true;
                        Check(WcEvents.GotoMap(WcEvents.WobMap, 1), "encounters: warp on foot to the WoB map (Narshe's door)");
                        return true;
                    }),
                    (6, () => { Walk(0, -1, 2.5f); return true; }),   // south, off the town's cells
                    (4f, () =>
                    {
                        var player = Field()?.player;
                        if (player == null) { Check(false, "encounters: no player"); WcEncounters.Paused = false; return false; }
                        int mapNow = UserDataManager.Instance()?.CurrentMapId ?? 0;
                        int rate = WcEncounters.RateHere(Field(), mapNow, player, false, out string where, out int ground);
                        // a step's work, timed: the field search a step used to make (FindObjectOfType<FieldMap>:
                        // walking stuttered on the Deck) and the rate lookup it makes now
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        for (int i = 0; i < 200; i++) Object.FindObjectOfType<FieldMap>();
                        double findMs = sw.Elapsed.TotalMilliseconds / 200;
                        var fcNow = Field();
                        sw.Restart();
                        for (int i = 0; i < 200; i++) WcEncounters.RateHere(fcNow, mapNow, player, false, out _, out _);
                        double rateMs = sw.Elapsed.TotalMilliseconds / 200;
                        Plugin.L.LogInfo($"[wc test] encounters: per step, the old field search {findMs:0.000} ms, the rate lookup now {rateMs:0.000} ms");
                        Check(rateMs < 0.5, $"encounters: a step's rate lookup {rateMs:0.000} ms (the old field search alone {findMs:0.000} ms)");
                        Check(rate == 0x60 || rate == 0xC0, $"encounters: zone 10's rate at {WcEvents.PlayerCell()}: {rate:X} ({where}; the game's ground value {ground})");
                        var rng = new System.Random(1234);
                        var lens = new List<int>();
                        int danger = 0, since = 0;
                        for (int i = 0; i < 3000; i++)
                        {
                            since++;
                            if (!WcEncounters.Advance(danger, rate, rng.Next(256), out danger)) continue;
                            lens.Add(since);
                            since = 0;
                            danger = 0;
                        }
                        double mean = lens.Count > 0 ? lens.Average() : 0, want = means.GetValueOrDefault(rate, 0);
                        Check(want > 0 && lens.Count >= 50 && Math.Abs(mean - want) <= 0.12 * want,
                              $"encounters: {lens.Count} battles in 3000 steps at rate {rate:X}: {mean:0.0} steps per battle (SNES {want}); shortest {(lens.Count > 0 ? lens.Min() : 0)}, longest {(lens.Count > 0 ? lens.Max() : 0)}");
                        WcEncounters.Paused = false;
                        battlesBefore = WcEncounters.Battles;
                        stepsBefore = WcEncounters.Steps;
                        scaledBefore = WcScaling.LastReport;
                        // the danger near its top (high byte 240: a battle on 94% of steps), so the walk's
                        // next steps bring one through the game's own CheckEncount and battle start (the
                        // mean steps per battle are checked above; Narshe's door leaves a few tiles to walk)
                        var lot = (HarmonyLib.AccessTools.Property(typeof(Last.Map.FieldController), "encountLot")?.GetValue(Field())
                                   as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<Last.Map.EncounterLot>();
                        Check(lot != null, "encounters: the field's encounter lot");
                        if (req.GetValueOrDefault("vanilla", "0") == "1")
                        {
                            // the PR's own random battle (the hook off, its threshold reached): whether the
                            // field's encounter start runs headless at all
                            WcEncounters.Off = true;
                            lot?.SetRestoreData(1, 1);
                            Plugin.L.LogInfo("[wc test] encounters: the hook off, the PR's own threshold reached");
                        }
                        else
                        {
                            lot?.SetRestoreData(0xF000, 70000);
                            WcEncounters.CancelForTest = true;   // the battle's start can't run headless
                            setUpBefore = WcEncounters.SetUp;
                        }
                        Walk(0, -1, 10);   // on with the hook live: the game's own steps and battle start
                        return true;
                    }),
                };
                // every 10 s: a battle from a step ends it; else on in another direction (2 minutes at most)
                var dirs = new[] { new Vector2(1, 0), new Vector2(0, -1), new Vector2(-1, 0), new Vector2(0, 1) };
                for (int k = 0; k < 12; k++)
                {
                    int round = k;
                    steps.Add((10f, () =>
                    {
                        // the game's own CheckEncount set the hook's battle up (its party picked, the
                        // battle preloaded); the test calls it off there (headless, the start would end the run)
                        bool fought = WcEncounters.SetUp > setUpBefore || (WcEncounters.Battles > battlesBefore && WcScaling.LastReport != scaledBefore);
                        if (fought || round == 11)
                        {
                            WcEncounters.CancelForTest = false;
                            Check(WcEncounters.Steps > stepsBefore, $"encounters: the walk's steps went through the hook ({WcEncounters.Steps - stepsBefore}; last {WcEncounters.Last})");
                            Check(fought, $"encounters: walking, the game set up a battle from a step ({WcEncounters.SetUp - setUpBefore} set up, {WcEncounters.Battles - battlesBefore} rolled, after {WcEncounters.Steps - stepsBefore} steps, {round * 10 + 10} s; last {WcEncounters.Last}; at {WcEvents.PlayerCell()})");
                            return false;
                        }
                        var d = dirs[round % dirs.Length];
                        Plugin.L.LogInfo($"[wc test] encounters: {WcEncounters.Steps - stepsBefore} steps, {WcEncounters.Battles - battlesBefore} battles so far; last {WcEncounters.Last}");
                        Walk(d.x, d.y, 10);
                        return true;
                    }));
                }
                return steps;
            }
            if (name == "atb")
            {
                // WC's (the SNES's) ATB (WcAtb): the game's own BattleProgressATBFF6.ATBCalc per second
                // (its gauge: 0-100) for a party member and a monster at a sweep of Speeds, the monster
                // at each of the PR's battle speeds, Haste and Slow, against the SNES's numbers (C2/09D2
                // CalcSpeed, C2/111B UpdateBattleTime: expected values from tests/test_wc.py's model);
                // then the gauges' real fill from 0 for 2.5 s. Measured before WcAtb: party
                // 0.260 x (Speed + 13), monsters 0.216 x (Speed + 21), Haste x1.5, the battle speed on both.
                int savedSpeed = -1, savedMode = -1;
                float t0 = 0;
                var g0 = new Dictionary<IntPtr, float>();
                var want0 = new Dictionary<IntPtr, double>();
                int[] speeds = { 1, 10, 30, 50, 100, 128 };
                double[] party = { 5.7773, 8.2533, 13.7555, 19.2577, 33.0133, 40.7164 };
                var monster = new Dictionary<int, double[]>   // the PR's battle speed 0-4 -> SNES Bat.Speed 6, 5, 3, 2, 1
                {
                    [0] = new[] { 3.0262, 4.1267, 7.1529, 9.904, 17.332, 21.4586 },
                    [1] = new[] { 3.5764, 4.952, 8.5284, 11.8298, 20.3582, 25.0351 },
                    [2] = new[] { 4.4018, 6.6027, 11.0044, 15.4062, 26.6857, 32.7381 },
                    [3] = new[] { 4.952, 7.428, 12.38, 17.332, 29.7119, 36.5897 },
                    [4] = new[] { 5.5022, 7.9782, 13.4804, 18.9826, 32.7381, 40.4412 },
                };
                bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol * Math.Max(1e-6, Math.Abs(b));
                return new List<(float, Func<bool>)>
                {
                    (0, () => { WcBattles.Queue(7); Plugin.L.LogInfo("[wc test] atb: party 7 queued"); return true; }),
                    (8, () =>
                    {
                        var battle = BattlePlugManager.Instance();
                        var prog = battle?.BattleProgress?.TryCast<BattleProgressATBFF6>();
                        var calc = HarmonyLib.AccessTools.Method(typeof(BattleProgressATBFF6), "ATBCalc");
                        var cfg = UserDataManager.Instance()?.Config;
                        if (prog == null || calc == null || cfg == null) { Check(false, $"atb: progress {prog != null}, calc {calc != null}, config {cfg != null}"); return false; }
                        savedSpeed = cfg.BattleSpeed;
                        savedMode = cfg.BattleModeIndex;
                        var hero = battle.GetPlayerUnits()?.ToArray().FirstOrDefault(u => u != null);
                        var foe = battle.GetEnemyUnits()?.ToArray().FirstOrDefault(u => u != null);
                        float Rate(BattleUnitData u) => (float)calc.Invoke(prog, new object[] { u }) / Mathf.Max(1e-6f, Time.deltaTime);
                        var hp = hero?.BattleUnitDataInfo?.Parameter;
                        var fp = foe?.BattleUnitDataInfo?.Parameter;
                        if (hp == null || fp == null) { Check(false, "atb: a party member and a monster"); return false; }
                        int heroAgi = hp.BaseAgility, foeAgi = fp.BaseAgility;
                        var got = new List<string>();
                        bool ok = true;
                        for (int i = 0; i < speeds.Length; i++)
                        {
                            hp.BaseAgility = speeds[i];
                            float r = Rate(hero);
                            got.Add($"{hp.ConfirmedAgility()}:{r:0.000}");
                            ok &= Near(r, party[i], 0.002);
                        }
                        Check(ok, $"atb: the party's fill per second = the SNES's ({string.Join(" ", got)}; SNES {string.Join(" ", party)})");
                        foreach (var kv in monster)
                        {
                            cfg.BattleSpeed = kv.Key;
                            got.Clear();
                            ok = true;
                            for (int i = 0; i < speeds.Length; i++)
                            {
                                fp.BaseAgility = speeds[i];
                                float r = Rate(foe);
                                got.Add($"{fp.ConfirmedAgility()}:{r:0.000}");
                                ok &= Near(r, kv.Value[i], 0.002);
                            }
                            Check(ok, $"atb: a monster's fill per second at the PR's battle speed {kv.Key} = the SNES's ({string.Join(" ", got)}; SNES {string.Join(" ", kv.Value)})");
                        }
                        cfg.BattleSpeed = savedSpeed;
                        fp.BaseAgility = foeAgi;
                        hp.BaseAgility = 50;
                        float normal = Rate(hero);
                        BattleUtility.AddCondition(hero, (Last.Defaine.ConditionType)15);   // Haste ("Heist")
                        float haste = Rate(hero);
                        BattleUtility.AddCondition(hero, (Last.Defaine.ConditionType)16);   // Slow (wins over Haste on the SNES)
                        float slow = Rate(hero);
                        Check(Near(normal, 19.2577, 0.002) && Near(haste, 25.2643, 0.002) && Near(slow, 9.6289, 0.002),
                              $"atb: Speed 50: {normal:0.000}/s, Haste {haste:0.000} (SNES 25.264: x1.3125), Slow {slow:0.000} (SNES 9.629: x0.5); statuses [{string.Join(",", hp.CurrentConditionList.ToArray().Select(c => c?.Id ?? 0))}]");
                        hp.BaseAgility = heroAgi;
                        // the gauges' real fill: Active mode, every gauge from 0, for 2.5 s
                        cfg.BattleModeIndex = 0;
                        t0 = Time.realtimeSinceStartup;
                        foreach (var u in battle.GetPlayerUnits().ToArray().Cast<BattleUnitData>().Concat(battle.GetEnemyUnits().ToArray()))
                        {
                            if (u == null) continue;
                            prog.ChangeATBGaugeByUnitData(u, 0);
                            g0[u.Pointer] = prog.GetAtbGaugeByUnitData(u);
                            want0[u.Pointer] = Rate(u);
                        }
                        return true;
                    }),
                    (2.5f, () =>
                    {
                        var battle = BattlePlugManager.Instance();
                        var prog = battle?.BattleProgress?.TryCast<BattleProgressATBFF6>();
                        float dt = Time.realtimeSinceStartup - t0;
                        if (prog != null)
                            foreach (var u in battle.GetPlayerUnits().ToArray().Cast<BattleUnitData>().Concat(battle.GetEnemyUnits().ToArray()))
                            {
                                if (u == null || !g0.TryGetValue(u.Pointer, out var a)) continue;
                                float b = prog.GetAtbGaugeByUnitData(u);
                                // the rate for the statuses the unit has now (Haste and Slow together don't
                                // last: the game keeps one of them a moment after both are added)
                                var calc = HarmonyLib.AccessTools.Method(typeof(BattleProgressATBFF6), "ATBCalc");
                                double want = (float)calc.Invoke(prog, new object[] { u }) / Mathf.Max(1e-6f, Time.deltaTime);
                                string statuses = string.Join(",", u.BattleUnitDataInfo?.Parameter?.CurrentConditionList?.ToArray().Select(c => c?.Id ?? 0) ?? new int[0]);
                                Check(b < 99.9f && (Near((b - a) / dt, want, 0.15) || Near((b - a) / dt, want0[u.Pointer], 0.15)),
                                      $"atb: real fill {(u.TryCast<BattlePlayerData>() != null ? "party" : "monster")} speed {u.BattleUnitDataInfo?.Parameter?.ConfirmedAgility()} [{statuses}]: {a:0.0} -> {b:0.0} in {dt:0.00} s ({(b - a) / dt:0.000}/s; ATBCalc {want:0.000}/s, at the start {want0[u.Pointer]:0.000}/s)");
                            }
                        var cfg = UserDataManager.Instance()?.Config;
                        if (cfg != null && savedMode >= 0) cfg.BattleModeIndex = savedMode;
                        if (cfg != null && savedSpeed >= 0) cfg.BattleSpeed = savedSpeed;
                        return false;
                    }),
                };
            }
            if (name == "status_ticks")
                return StatusTicksPlan();
            if (name == "turn_cycle")
                return TurnCyclePlan();
            if (name == "status_timers")
                return StatusTimersPlan();
if (name == "enemy_ai")
            {
                // every monster runs its WC script in the plugin (WcEnemyAi, SnesAi.cs; the user, 2026-11-12:
                // "please make all enemies/bosses behave like WC SNES"): Atma at Doma (the user's seed:
                // party 410 -> 20036), the party kept alive on auto. Its first turn is only the flash (no
                // act: the PR's "nothing"), its rotation casts WC's scaled placeholders (7601-7607 swapped
                // by WcAbilityScaling) or N. Cross, being hit runs its counter (the shared variable 3
                // counts hits; the 13th sets the charge: "Unknown light surrounded Atma!", two idle turns,
                // the big spell)
                int party = 0, turnsAt = 0, countersAt = 0;
                float lastDiag = 0;
                var turns = new List<string>();
                var steps = new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        Check(WcSeeds.Active?.SnesAi != null && WcSeeds.Active.SnesAi.Scripts.Count == 384,
                              $"enemy_ai: the seed carries every monster's script ({WcSeeds.Active?.SnesAi?.Scripts.Count ?? 0})");
                        int want = WcSeeds.Active != null && WcSeeds.Active.Battles.TryGetValue(410, out var to) && to.Length > 0 ? to[0] : 410;
                        party = want;
                        Check(MasterManager.Instance.GetData<global::Last.Data.Master.MonsterParty>(party) != null, $"enemy_ai: Atma's party {party}");
                        turnsAt = WcEnemyAi.Turns; countersAt = WcEnemyAi.Counters;
                        WcBattles.Queue(party);
                        return true;
                    }),
                    (8, () =>
                    {
                        var battle = BattlePlugManager.Instance();
                        Check(battle != null && battle.IsBattle(), "enemy_ai: in battle");
                        var ui = Last.UI.BattleUIManager.Instance;
                        ui?.SetIsAuto(true);
                        // the party kept alive: what the monster does is the test, not the outcome
                        string lastTurn = "", lastCounter = "";
                        recorder = () =>
                        {
                            var b = BattlePlugManager.Instance();
                            if (b == null || !b.IsBattle()) return;
                            foreach (var p in b.GetPlayerUnits())
                            {
                                var pr = p?.BattleUnitDataInfo?.Parameter;
                                // no spell kills them: current HP is capped at max HP, so max HP too (as WcScaling sets monsters')
                                if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999)
                                {
                                    if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999;
                                    pr.CurrentHP = 9999;
                                }
                            }
                            if (WcEnemyAi.LastTurn != lastTurn) { lastTurn = WcEnemyAi.LastTurn; turns.Add(lastTurn); }
                            if (WcEnemyAi.LastCounter != lastCounter) lastCounter = WcEnemyAi.LastCounter;
                            // every 2 s: each monster's act-select state (where a stalled battle waits)
                            if (Time.realtimeSinceStartup - lastDiag > 2)
                            {
                                lastDiag = Time.realtimeSinceStartup;
                                var parts = new List<string>();
                                foreach (var e in b.GetEnemyUnits())
                                {
                                    var ed = e?.TryCast<BattleEnemyData>();
                                    var ctl = e?.BattleUnitDataInfo?.ActSelectController?.TryCast<ActSelectEnemyController>();
                                    if (ed == null) continue;
                                    parts.Add($"monster {ed.GetMonster()?.Id} unique {ed.UniqueId} HP {e.BattleUnitDataInfo?.Parameter?.CurrentHP} playedAI {ed.IsPlayedAI} " +
                                              (ctl == null ? "no controller" : $"turn {ctl.GetActTurn()} normal {ctl.IsReadyActNormal()} counter {ctl.IsReadyActCounter()} final {ctl.IsReadyActFinalAttack()} viewMes {ctl.IsViewMes()}/{ctl.IsViewMesAfterEffect()}"));
                                }
                                var hp = new List<string>();
                                foreach (var pu in b.GetPlayerUnits()) hp.Add($"{pu?.BattleUnitDataInfo?.Parameter?.CurrentHP}");
                                Plugin.L.LogInfo($"[wc test] enemy_ai state: turns {WcEnemyAi.Turns - turnsAt}, counters {WcEnemyAi.Counters - countersAt}; party HP {string.Join("/", hp)}; {string.Join("; ", parts)}");
                            }
                        };
                        return true;
                    }),
                };
                for (int k = 0; k < 24; k++)
                {
                    int round = k;
                    steps.Add((5f, () =>
                    {
                        var ui = Last.UI.BattleUIManager.Instance;
                        if (ui != null && !ui.IsAuto()) ui.SetIsAuto(true);
                        if (round < 23) return true;
                        recorder = null;
                        int t = WcEnemyAi.Turns - turnsAt, c = WcEnemyAi.Counters - countersAt;
                        Check(t >= 4, $"enemy_ai: Atma's turns ran its WC script ({t} turns; last: {WcEnemyAi.LastTurn})");
                        Check(c >= 1, $"enemy_ai: being hit ran its counter script ({c} counters; last: {WcEnemyAi.LastCounter})");
                        // at this level Atma's HP (4920) is under its 7680 check: its low-HP rotation from the
                        // first turn (WC's fire+1/water+1/earth+2, then fire+1/non-elem+1, ...), all placeholders
                        Check(turns.Count >= 4 && turns.Take(4).All(x => x.Contains("attack 37") || x.Contains("attack 46") || x.Contains("attack 41") || x.Contains("attack 4f")),
                              $"enemy_ai: its low-HP rotation casts WC's placeholders: {string.Join(" | ", turns.Take(4))}");
                        // the 13th hit's charge: "Unknown light surrounded Atma!" (the PR's own line for it)
                        Check(WcEnemyAi.LinesSeen.Contains("E0977_00_554_b_08"),
                              $"enemy_ai: the charge's battle line showed ({string.Join(", ", WcEnemyAi.LinesSeen)})");
                        foreach (var x in turns) Plugin.L.LogInfo("[wc test] enemy_ai turn: " + x);
                        return true;
                    }));
                }
                int finalsAt = 0, winsAt = 0;
                bool ended = false;
                steps.Add((1f, () =>
                {
                    finalsAt = WcEnemyAi.Finals;
                    winsAt = WcScaling.Victories;
                    foreach (var e in BattlePlugManager.Instance()?.GetEnemyUnits() ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BattleEnemyData>(0))
                    {
                        var pr = e?.BattleUnitDataInfo?.Parameter;
                        if (pr != null && pr.CurrentHP > 1) pr.CurrentHP = 1;   // the party's next hit kills it
                    }
                    return true;
                }));
                for (int k = 0; k < 8; k++)
                {
                    int round = k;
                    steps.Add((5f, () =>
                    {
                        bool over = WcScaling.Victories > winsAt;   // won (the result screen waits for a button headless)
                        if (ended || (!over && round < 7)) return true;
                        ended = true;
                        recorder = null;
                        Check(WcEnemyAi.Finals > finalsAt && WcEnemyAi.LastFinal.Contains("=> 735"),
                              $"enemy_ai: Atma's death runs its death block: everyone leaves like a boss ({WcEnemyAi.LastFinal})");
                        Check(over, "enemy_ai: the battle was won after Atma's death");
                        return true;
                    }));
                }
                return steps;
            }
            if (name == "entry_exit_probe")
            {
                // what the PR's own conditions do to a monster (for WC's F5 entries/exits): Escape (3) on A,
                // then lifted; NotTargeting (667) on B; does the party still hit B, does A come back, does
                // the battle end when C dies with B untargetable and A gone
                int party = 0, winsAt = 0, turnsAtAdd = 0, addedCell = 0;
                BattleEnemyData A = null, B = null, C = null;
                int hpB = 0, hpC = 0;
                string State(BattleEnemyData e)
                {
                    if (e == null) return "none";
                    var b = BattlePlugManager.Instance();
                    var pr = e.BattleUnitDataInfo?.Parameter;
                    var conds = new List<string>();
                    if (pr?.CurrentConditionList != null) foreach (var c in pr.CurrentConditionList) if (c != null) conds.Add(((int)c.ConditionType).ToString());
                    bool listed = false, dust = false;
                    if (b != null)
                    {
                        foreach (var u in b.GetEnemyUnits()) if (u != null && u.Pointer == e.Pointer) listed = true;
                        foreach (var u in b.GetDustEnemyUnits()) if (u != null && u.Pointer == e.Pointer) dust = true;
                    }
                    var ent = b?.EventCommand?.GetEnemyEntity(e.UniqueId);
                    return $"unique {e.UniqueId} HP {pr?.CurrentHP} conditions [{string.Join(",", conds)}] listed {listed} dust {dust} entity {(ent == null ? "none" : ent.gameObject.activeInHierarchy.ToString())}";
                }
                return new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        foreach (var kv in MasterManager.Instance.GetList<global::Last.Data.Master.MonsterParty>())
                        {
                            var p = kv.Value;
                            if (p == null || p.Id >= 600) continue;
                            var ms = new[] { p.Monster1, p.Monster2, p.Monster3, p.Monster4, p.Monster5, p.Monster6, p.Monster7, p.Monster8, p.Monster9 }.Where(m => m > 0).ToArray();
                            if (ms.Length == 3) { party = p.Id; break; }
                        }
                        Check(party > 0, $"entry_exit_probe: a party of three ({party})");
                        if (party <= 0) return false;
                        winsAt = WcScaling.Victories;
                        WcBattles.Queue(party);
                        return true;
                    }),
                    (8, () =>
                    {
                        var b = BattlePlugManager.Instance();
                        Check(b != null && b.IsBattle(), "entry_exit_probe: in battle");
                        if (b == null) return false;
                        var es = new List<BattleEnemyData>();
                        foreach (var u in b.GetEnemyUnits()) { var ed = u?.TryCast<BattleEnemyData>(); if (ed != null) es.Add(ed); }
                        es = es.OrderBy(x => x.UniqueId).ToList();
                        if (es.Count < 3) { Check(false, $"entry_exit_probe: three monsters ({es.Count})"); return false; }
                        A = es[0]; B = es[1]; C = es[2];
                        foreach (var e in es) { var pr = e.BattleUnitDataInfo.Parameter; if (pr.BaseMaxHp < 60000) pr.BaseMaxHp = 60000; pr.CurrentHP = 50000; }
                        recorder = () =>
                        {
                            var bb = BattlePlugManager.Instance();
                            if (bb == null || !bb.IsBattle()) return;
                            foreach (var u in bb.GetPlayerUnits())
                            {
                                var pr = u?.BattleUnitDataInfo?.Parameter;
                                if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999) { if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999; pr.CurrentHP = 9999; }
                            }
                        };
                        Last.UI.BattleUIManager.Instance?.SetIsAuto(true);
                        Plugin.L.LogInfo("[wc test] entry_exit_probe: before: A " + State(A) + " | B " + State(B) + " | C " + State(C));
                        b.EventCommand.AddConditionEnemy(A.UniqueId, 3);
                        Plugin.L.LogInfo("[wc test] entry_exit_probe: A given Escape (3): " + State(A));
                        return true;
                    }),
                    (3, () =>
                    {
                        Plugin.L.LogInfo("[wc test] entry_exit_probe: A 3 s after Escape: " + State(A));
                        var b = BattlePlugManager.Instance();
                        b?.EventCommand?.AddConditionEnemy(B.UniqueId, 667);
                        hpB = B.BattleUnitDataInfo.Parameter.CurrentHP; hpC = C.BattleUnitDataInfo.Parameter.CurrentHP;
                        Plugin.L.LogInfo("[wc test] entry_exit_probe: B given NotTargeting (667): " + State(B));
                        return true;
                    }),
                    (12, () =>
                    {
                        int dB = hpB - B.BattleUnitDataInfo.Parameter.CurrentHP, dC = hpC - C.BattleUnitDataInfo.Parameter.CurrentHP;
                        Plugin.L.LogInfo($"[wc test] entry_exit_probe: 12 s of the party on auto: B (untargetable) lost {dB} HP, C lost {dC}; A {State(A)}");
                        var b = BattlePlugManager.Instance();
                        b?.EventCommand?.RecoveryConditionEnemy(A.UniqueId, 3);
                        Plugin.L.LogInfo("[wc test] entry_exit_probe: A's Escape lifted: " + State(A));
                        return true;
                    }),
                    (4, () =>
                    {
                        Plugin.L.LogInfo("[wc test] entry_exit_probe: A 4 s later: " + State(A) + " | B " + State(B) + " | C " + State(C));
                        // a monster brought in the PR's way (its AddEnemy abilities: EventCommand.InBattleEnemy)
                        // into a cell no monster has: does it appear there and take turns?
                        var b = BattlePlugManager.Instance();
                        var used = new HashSet<int>();
                        foreach (var u in b.GetEnemyUnits()) { var ed = u?.TryCast<BattleEnemyData>(); if (ed != null) used.Add(ed.UniqueId); }
                        int cell = Enumerable.Range(1, 9).FirstOrDefault(k => !used.Contains(k));
                        int mon = A.GetMonster()?.Id ?? 0;
                        turnsAtAdd = WcEnemyAi.Turns;
                        Plugin.L.LogInfo($"[wc test] entry_exit_probe: InBattleEnemy({mon}, cell {cell}); enemies before: {string.Join(",", used.OrderBy(x => x))}");
                        if (cell > 0 && mon > 0) b.EventCommand.InBattleEnemy(mon, cell);
                        addedCell = cell;
                        return true;
                    }),
                    (8, () =>
                    {
                        var b = BattlePlugManager.Instance();
                        var now = new List<string>();
                        BattleEnemyData added = null;
                        foreach (var u in b.GetEnemyUnits())
                        {
                            var ed = u?.TryCast<BattleEnemyData>();
                            if (ed == null) continue;
                            now.Add($"{ed.UniqueId}:{ed.GetMonster()?.Id}");
                            if (ed.UniqueId == addedCell) added = ed;
                        }
                        Plugin.L.LogInfo($"[wc test] entry_exit_probe: 8 s after InBattleEnemy: enemies {string.Join(",", now)}; the new one: {State(added)}; " +
                                         $"turns since {WcEnemyAi.Turns - turnsAtAdd} (last {WcEnemyAi.LastTurn})");
                        return true;
                    }),
                    (1, () =>
                    {
                        // A gone again, B untargetable: C's death alone ends the battle?
                        var b = BattlePlugManager.Instance();
                        b?.EventCommand?.AddConditionEnemy(A.UniqueId, 3);
                        C.BattleUnitDataInfo.Parameter.CurrentHP = 1;
                        winsAt = WcScaling.Victories;
                        return true;
                    }),
                    (15, () =>
                    {
                        var b = BattlePlugManager.Instance();
                        bool won = WcScaling.Victories > winsAt;
                        Plugin.L.LogInfo($"[wc test] entry_exit_probe: 15 s after C at 1 HP: won {won}, in battle {b != null && b.IsBattle()}; A {State(A)} | B {State(B)} | C {State(C)}");
                        recorder = null;
                        if (b != null && b.IsBattle() && !won && b.BattleEndJugment != null)
                        {
                            b.BattleEndJugment.SetEnabled(true);
                            b.BattleEndJugment.CurrentEndJugmentType = BattleEndJugment.EndJugmentType.Normal;
                            b.BattleEndJugment.isForce = true;
                        }
                        return true;
                    }),
                    (6, () => false),
                };
            }
            if (name == "enemy_ai_sweep" || name.StartsWith("enemy_ai_sweep@"))
            {
                // many fights in a row (the plugin's hooks must never stall a battle): a spread of the
                // parties with a monster whose turns the plugin runs (the seed's list), each on auto for
                // a while with both sides kept alive, then ended the way the PR's BattleForceExit ends
                // one (BattleEndJugment.isForce); a fight whose monsters took no turn is a stall
                int want = name.Contains("@") ? int.Parse(name.Substring(name.IndexOf('@') + 1)) : 30;
                var parties = new List<int>();
                var report = new List<string>();
                var idle = new List<string>();
                int turnsAt = 0, countersAt = 0, current = 0, done = 0, notStarted = 0, notEnded = 0;
                string Mons(BattlePlugManager b)
                {
                    var ids = new List<string>();
                    foreach (var e in b.GetEnemyUnits()) ids.Add($"{e?.GetMonster()?.Id ?? 0}");
                    return string.Join("/", ids);
                }
                var steps = new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        var d = WcSeeds.Active?.SnesAi;
                        Check(d != null && d.Plugin.Count > 0, $"enemy_ai_sweep: the seed lists the monsters the plugin runs ({d?.Plugin.Count ?? 0})");
                        if (d == null || d.Plugin.Count == 0) return false;
                        var seenSets = new HashSet<string>();
                        var all = new List<int>();
                        foreach (var kv in MasterManager.Instance.GetList<global::Last.Data.Master.MonsterParty>())
                        {
                            var p = kv.Value;
                            if (p == null || p.Id >= 2000) continue;
                            var ms = new[] { p.Monster1, p.Monster2, p.Monster3, p.Monster4, p.Monster5, p.Monster6 }.Where(m => m > 0).ToArray();
                            if (ms.Length == 0 || !ms.Any(m => d.Plugin.Contains(m - 1))) continue;
                            if (!seenSets.Add(string.Join(",", ms.OrderBy(x => x)))) continue;
                            all.Add(p.Id);
                        }
                        all.Sort();
                        for (int i = 0; i < want && all.Count > 0; i++)
                        {
                            int at = (int)((long)i * all.Count / want);
                            if (!parties.Contains(all[at])) parties.Add(all[at]);
                        }
                        Plugin.L.LogInfo($"[wc test] enemy_ai_sweep: {parties.Count} of {all.Count} parties: {string.Join(", ", parties)}");
                        recorder = () =>
                        {
                            var b = BattlePlugManager.Instance();
                            if (b == null || !b.IsBattle()) return;
                            foreach (var u in b.GetPlayerUnits())
                            {
                                var pr = u?.BattleUnitDataInfo?.Parameter;
                                if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999) { if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999; pr.CurrentHP = 9999; }
                            }
                            // no win (the result screen waits for a button headless): enemies get HP no
                            // early party can take in one hit, topped up every frame
                            foreach (var u in b.GetEnemyUnits())
                            {
                                var pr = u?.BattleUnitDataInfo?.Parameter;
                                if (pr == null || pr.CurrentHP <= 0) continue;
                                if (pr.BaseMaxHp < 60000) pr.BaseMaxHp = 60000;
                                int max = pr.ConfirmedMaxHp();
                                if (pr.CurrentHP < max) pr.CurrentHP = max;
                            }
                        };
                        return true;
                    }),
                };
                for (int k = 0; k < want; k++)
                {
                    int i = k;
                    steps.Add((1f, () =>
                    {
                        if (i >= parties.Count) return true;
                        current = parties[i];
                        turnsAt = WcEnemyAi.Turns; countersAt = WcEnemyAi.Counters;
                        WcBattles.Queue(current);
                        return true;
                    }));
                    steps.Add((7f, () =>
                    {
                        if (i >= parties.Count) return true;
                        var b = BattlePlugManager.Instance();
                        if (b == null || !b.IsBattle()) { notStarted++; report.Add($"party {current}: did not start"); return true; }
                        Last.UI.BattleUIManager.Instance?.SetIsAuto(true);
                        return true;
                    }));
                    steps.Add((18f, () =>
                    {
                        if (i >= parties.Count) return true;
                        var b = BattlePlugManager.Instance();
                        if (b == null || !b.IsBattle()) return true;
                        int t = WcEnemyAi.Turns - turnsAt, c = WcEnemyAi.Counters - countersAt;
                        string mons = Mons(b);
                        report.Add($"party {current} [{mons}]: {t} turns, {c} counters; last turn {WcEnemyAi.LastTurn}");
                        if (t == 0) idle.Add($"{current} [{mons}]");
                        // the judge honours isForce only when enabled and of the Normal type (a battle
                        // script can switch either: party 71's 9026 does)
                        var j = b.BattleEndJugment;
                        if (j != null)
                        {
                            j.SetEnabled(true);
                            j.CurrentEndJugmentType = BattleEndJugment.EndJugmentType.Normal;
                            j.isForce = true;
                        }
                        return true;
                    }));
                    for (int w = 0; w < 6; w++)
                    {
                        int wait = w;
                        steps.Add((3f, () =>
                        {
                            if (i >= parties.Count || i < done) return true;   // done: ended already
                            var b = BattlePlugManager.Instance();
                            bool still = b != null && b.IsBattle();
                            if (still && wait < 5) return true;
                            if (still) { notEnded++; report.Add($"party {current}: still in battle 18 s after the forced end"); }
                            done++;
                            return true;
                        }));
                    }
                }
                steps.Add((1f, () =>
                {
                    recorder = null;
                    foreach (var r in report) Plugin.L.LogInfo("[wc test] enemy_ai_sweep: " + r);
                    Check(parties.Count > 0 && notStarted == 0, $"enemy_ai_sweep: every fight started ({parties.Count - notStarted} of {parties.Count})");
                    Check(idle.Count == 0, $"enemy_ai_sweep: the monsters took WC turns in every fight (none in: {string.Join(", ", idle)})");
                    Check(notEnded == 0, $"enemy_ai_sweep: every fight ended when told to ({notEnded} did not)");
                    return true;
                }));
                return steps;
            }
            if (name.StartsWith("enemy_ai_each@"))
            {
                // several monsters' fights in one run (PR monster ids after the @, "+"-separated; the request
                // splits checks at commas): each monster's first party, 30 s on auto with both sides kept alive
                // (monsters get HP no early party takes in a hit, topped up), its turns, counters and
                // entries/exits logged; then "Nk" lets the party kill the monster, "No" its partners, and 15 s
                // more show what the deaths bring in; the battle is then ended as the PR's BattleForceExit
                // ends one. A fight whose monsters took no turn fails.
                var list = name.Substring(14).Split('+', ' ').Where(x => x.Trim().Length > 0).Select(x => x.Trim()).ToList();
                var steps = new List<(float, Func<bool>)>();
                int turnsAt = 0, countersAt = 0, entriesAt = 0, finalsAt = 0, party = 0;
                var exposed = new HashSet<int>();   // unique ids no longer kept alive
                var low = new HashSet<int>();       // unique ids held at 2000 HP (their low-HP branches)
                string Enemies(BattlePlugManager b)
                {
                    var l = new List<string>();
                    foreach (var e in b.GetEnemyUnits())
                    {
                        var ed = e?.TryCast<BattleEnemyData>();
                        if (ed == null) continue;
                        var pr = ed.BattleUnitDataInfo?.Parameter;
                        var conds = new List<string>();
                        if (pr?.CurrentConditionList != null) foreach (var c in pr.CurrentConditionList) if (c != null) conds.Add(((int)c.ConditionType).ToString());
                        l.Add($"{ed.UniqueId}:{ed.GetMonster()?.Id} HP {pr?.CurrentHP} [{string.Join(",", conds)}]");
                    }
                    return string.Join("; ", l);
                }
                recorder = () =>
                {
                    var b = BattlePlugManager.Instance();
                    if (b == null || !b.IsBattle()) return;
                    foreach (var u in b.GetPlayerUnits())
                    {
                        var pr = u?.BattleUnitDataInfo?.Parameter;
                        if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999) { if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999; pr.CurrentHP = 9999; }
                    }
                    foreach (var u in b.GetEnemyUnits())
                    {
                        var ed = u?.TryCast<BattleEnemyData>();
                        var pr = ed?.BattleUnitDataInfo?.Parameter;
                        if (pr == null || pr.CurrentHP <= 0 || exposed.Contains(ed.UniqueId)) continue;
                        if (pr.BaseMaxHp < 30000) pr.BaseMaxHp = 30000;
                        int max = low.Contains(ed.UniqueId) ? 2000 : pr.ConfirmedMaxHp();
                        if (pr.CurrentHP < max) pr.CurrentHP = max;
                    }
                };
                foreach (var item in list)
                {
                    char mode = char.IsLetter(item[item.Length - 1]) ? item[item.Length - 1] : ' ';
                    int monster = int.Parse(mode == ' ' ? item : item.Substring(0, item.Length - 1));
                    steps.Add((1f, () =>
                    {
                        party = 0;
                        exposed.Clear();
                        low.Clear();
                        foreach (var kv in MasterManager.Instance.GetList<global::Last.Data.Master.MonsterParty>())
                        {
                            var p = kv.Value;
                            if (p == null || p.Id >= 2000) continue;
                            if (new[] { p.Monster1, p.Monster2, p.Monster3, p.Monster4, p.Monster5, p.Monster6, p.Monster7, p.Monster8, p.Monster9 }.Contains(monster)) { party = p.Id; break; }
                        }
                        Check(party > 0, $"enemy_ai_each@{monster}: a party with it ({party})");
                        if (party <= 0) return true;
                        turnsAt = WcEnemyAi.Turns; countersAt = WcEnemyAi.Counters; entriesAt = WcEnemyAi.Entries; finalsAt = WcEnemyAi.Finals;
                        WcBattles.Queue(party);
                        return true;
                    }));
                    steps.Add((7f, () =>
                    {
                        if (party <= 0) return true;
                        var b = BattlePlugManager.Instance();
                        Check(b != null && b.IsBattle(), $"enemy_ai_each@{monster}: in battle (party {party})");
                        Last.UI.BattleUIManager.Instance?.SetIsAuto(true);
                        if (b != null) Plugin.L.LogInfo($"[wc test] enemy_ai_each@{monster}: start: {Enemies(b)}");
                        return true;
                    }));
                    for (int k = 0; k < 11; k++)
                    {
                        int round = k;
                        steps.Add((5f, () =>
                        {
                            if (party <= 0) return true;
                            var b = BattlePlugManager.Instance();
                            var ui = Last.UI.BattleUIManager.Instance;
                            if (ui != null && !ui.IsAuto()) ui.SetIsAuto(true);
                            if (b == null || !b.IsBattle()) return true;
                            Plugin.L.LogInfo($"[wc test] enemy_ai_each@{monster}: {5 * (round + 1)} s: {Enemies(b)}");
                            if (round == 5 && mode != ' ')
                            {
                                // the deaths: the monster itself (k) or its partners (o), at 1 HP for the party to finish
                                foreach (var u in b.GetEnemyUnits())
                                {
                                    var ed = u?.TryCast<BattleEnemyData>();
                                    if (ed == null) continue;
                                    bool it = ed.GetMonster()?.Id == monster;
                                    if ((mode == 'o') == it) continue;
                                    var pr = ed.BattleUnitDataInfo.Parameter;
                                    if (mode == 'l') { low.Add(ed.UniqueId); pr.CurrentHP = 2000; continue; }
                                    exposed.Add(ed.UniqueId);
                                    if (pr.CurrentHP > 1) pr.CurrentHP = 1;
                                }
                                Plugin.L.LogInfo($"[wc test] enemy_ai_each@{monster}: {(mode == 'k' ? "it at 1 HP" : mode == 'l' ? "it held at 2000 HP" : "its partners at 1 HP")} " +
                                                 $"(units {string.Join(",", exposed.Concat(low))})");
                            }
                            if (round < 10) return true;
                            int t = WcEnemyAi.Turns - turnsAt;
                            Check(t > 0, $"enemy_ai_each@{monster}: its fight's monsters took WC turns ({t}; {WcEnemyAi.Counters - countersAt} counters, " +
                                         $"{WcEnemyAi.Entries - entriesAt} entries/exits, last {WcEnemyAi.LastEntry}; {WcEnemyAi.Finals - finalsAt} deaths)");
                            var j = b.BattleEndJugment;
                            if (j != null) { j.SetEnabled(true); j.CurrentEndJugmentType = BattleEndJugment.EndJugmentType.Normal; j.isForce = true; }
                            return true;
                        }));
                    }
                    steps.Add((8f, () =>
                    {
                        var b = BattlePlugManager.Instance();
                        if (b != null && b.IsBattle()) Plugin.L.LogInfo($"[wc test] enemy_ai_each@{monster}: still in battle 8 s after the forced end");
                        return true;
                    }));
                }
                steps.Add((1f, () => { recorder = null; return true; }));
                return steps;
            }
            if (name.StartsWith("enemy_ai@"))
            {
                // any monster that runs its WC script in the plugin: a party with it (PR monster id after
                // the @), the party kept alive on auto for 45 s: its turns and counters run the
                // interpreter (logged), then it dies (its death block, if any)
                int monster = int.Parse(name.Substring(9)), party = 0, turnsAt = 0, countersAt = 0, finalsAt = 0, winsAt = 0, queuedAt = 0, shownAt = 0, seenAt = 0;
                bool ended = false;
                var steps = new List<(float, Func<bool>)>
                {
                    (2, () =>
                    {
                        foreach (var kv in MasterManager.Instance.GetList<global::Last.Data.Master.MonsterParty>())
                        {
                            var p = kv.Value;
                            if (p == null || p.Id >= 2000) continue;
                            if (new[] { p.Monster1, p.Monster2, p.Monster3, p.Monster4, p.Monster5, p.Monster6, p.Monster7, p.Monster8, p.Monster9 }.Contains(monster)) { party = p.Id; break; }
                        }
                        Check(party > 0, $"enemy_ai@{monster}: a party with it ({party})");
                        if (party <= 0) return false;
                        turnsAt = WcEnemyAi.Turns; countersAt = WcEnemyAi.Counters; finalsAt = WcEnemyAi.Finals; winsAt = WcScaling.Victories;
                        queuedAt = WcEnemyAi.LinesQueued; shownAt = WcEnemyAi.LinesShown; seenAt = WcEnemyAi.LinesSeen.Count;
                        WcBattles.Queue(party);
                        return true;
                    }),
                    (8, () =>
                    {
                        var battle = BattlePlugManager.Instance();
                        Check(battle != null && battle.IsBattle(), $"enemy_ai@{monster}: in battle (party {party})");
                        Last.UI.BattleUIManager.Instance?.SetIsAuto(true);
                        recorder = () =>
                        {
                            var b = BattlePlugManager.Instance();
                            if (b == null || !b.IsBattle()) return;
                            foreach (var p in b.GetPlayerUnits())
                            {
                                var pr = p?.BattleUnitDataInfo?.Parameter;
                                if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999) { if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999; pr.CurrentHP = 9999; }
                            }
                        };
                        return true;
                    }),
                };
                for (int k = 0; k < 9; k++)
                {
                    int round = k;
                    steps.Add((5f, () =>
                    {
                        var ui = Last.UI.BattleUIManager.Instance;
                        if (ui != null && !ui.IsAuto()) ui.SetIsAuto(true);
                        if (round < 8) return true;
                        Check(WcEnemyAi.Turns > turnsAt, $"enemy_ai@{monster}: its turns ran its WC script ({WcEnemyAi.Turns - turnsAt}; last {WcEnemyAi.LastTurn})");
                        Plugin.L.LogInfo($"[wc test] enemy_ai@{monster}: {WcEnemyAi.Counters - countersAt} counters (last {WcEnemyAi.LastCounter})");
                        if (WcEnemyAi.LinesQueued > queuedAt)
                            Check(WcEnemyAi.LinesShown > shownAt, $"enemy_ai@{monster}: its battle lines showed ({WcEnemyAi.LinesQueued - queuedAt} filed; " +
                                  $"shown: {string.Join(", ", WcEnemyAi.LinesSeen.Skip(seenAt))})");
                        foreach (var e in BattlePlugManager.Instance()?.GetEnemyUnits() ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BattleEnemyData>(0))
                        {
                            var pr = e?.BattleUnitDataInfo?.Parameter;
                            if (pr != null && pr.CurrentHP > 1) pr.CurrentHP = 1;
                        }
                        return true;
                    }));
                }
                for (int k = 0; k < 8; k++)
                {
                    int round = k;
                    steps.Add((5f, () =>
                    {
                        bool over = WcScaling.Victories > winsAt;
                        if (ended || (!over && round < 7)) return true;
                        ended = true;
                        recorder = null;
                        Plugin.L.LogInfo($"[wc test] enemy_ai@{monster}: {WcEnemyAi.Finals - finalsAt} deaths (last {WcEnemyAi.LastFinal}); " +
                                         (over ? "won" : "not won in time (an evasive monster the party keeps missing)"));
                        return true;
                    }));
                }
                return steps;
            }
            if (name == "ability_scaling")
            {
                // WC's ability scaling (-ase) as monsters act (WcAbilityScaling; the user's seed
                // gaadl68ix7j4, 2026-11-11: its Sandhorse always cast Wind Slash). (1) the tier
                // formula against WC's assembly (expected digits from a model of
                // enemy_script_custom_commands.py: rolls 0, 0x19, 0x59, 0xb2, 0xf2); (2) in a battle
                // with a Sandhorse (its script casts WIND1 = ability 7613): the act the game builds
                // for it, many times, holds WC's spell mix for its level, aimed at the party;
                // (3) its own AI turns swap the spell too.
                int swapsBefore = 0, natural = 0, party = 0;
                BattleUnitData horse = null;
                return new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        const string want = "00012 00123 01234 00012 00123 01234 00123 01234 12345 00123 01234 12345 01234 12345 23456 12345 23456 34567 34567 45678 56789 56789 6789a 789ab 6789a 789ab 89abb 6789a 789ab 89abb 6789a 789ab 89abb 6789a 789ab 89abb 00012 00123 01234 00012 00123 01234 00012 00123 01234 00123 01234 12345 00123 01234 12345 01234 12345 23456 23456 34567 45678 56789 6789a 789ab 56789 6789a 789ab 56789 6789a 789ab 6789a 789ab 89abb 6789a 789ab 89abb";
                        var got = new List<string>();
                        foreach (var f in new[] { 2.0, 2.5 })
                            foreach (var lv in new[] { 1, 4, 5, 9, 10, 16, 27, 39, 40, 43, 44, 99 })
                                for (int off = 0; off < 3; off++)
                                    got.Add(string.Concat(new[] { 0, 0x19, 0x59, 0xb2, 0xf2 }.Select(r => WcAbilityScaling.Tier(lv, off, f, r).ToString("x"))));
                        Check(string.Join(" ", got) == want, $"ability scaling: WC's tier formula ({string.Join(" ", got)})");
                        var s = WcSeeds.Active?.Scaling;
                        Check(s != null && s.Base == 7600 && s.Count == 36 && s.Tiers.Length == 12,
                              $"ability scaling: the seed's tiers ({(s == null ? "none" : $"base {s.Base}, {s.Tiers.Length} categories, factor {s.Factor}")})");
                        foreach (var kv in MasterManager.Instance.GetList<MonsterParty>())
                        {
                            var p = kv.Value;
                            if (p == null) continue;
                            if (new[] { p.Monster1, p.Monster2, p.Monster3, p.Monster4, p.Monster5, p.Monster6 }.Contains(96)) { party = p.Id; break; }
                        }
                        Check(party > 0, $"ability scaling: a party with a Sandhorse ({party})");
                        if (party <= 0 || s == null) return false;
                        WcBattles.Queue(party);
                        Plugin.L.LogInfo($"[wc test] ability_scaling: party {party} queued");
                        return true;
                    }),
                    (8, () =>
                    {
                        var battle = BattlePlugManager.Instance();
                        Check(battle != null && battle.IsBattle(), "ability scaling: in battle");
                        horse = battle?.GetEnemyUnits()?.ToArray().FirstOrDefault(u => u?.GetMonster()?.Id == 96);
                        var act = horse?.BattleUnitDataInfo?.ActSelectController?.GetAct()?.TryCast<ActSelectEnemy>();
                        var create = HarmonyLib.AccessTools.Method(typeof(ActSelectEnemy), "CreateActData");
                        if (act == null || create == null) { Check(false, $"ability scaling: the Sandhorse's act ({horse != null}, {act != null}, {create != null})"); return false; }
                        var s = WcSeeds.Active.Scaling;
                        int level = horse.BattleUnitDataInfo.Parameter.ConfirmedLevel();
                        // WC's odds for each spell: tier = base + 1 (WIND1) + (x - 2), weights 25/64/89/64/14 of 256
                        double per = s.Factor + 3;
                        int baseTier = level >= (int)(per * 8) ? 8 : level * 2 / (int)(per * 2);
                        var odds = new Dictionary<int, double>();
                        int[] weights = { 25, 64, 89, 64, 14 };
                        for (int x = 0; x < 5; x++)
                        {
                            var pool = s.Tiers[4][Math.Clamp(baseTier + 1 + x - 2, 0, 11)];
                            foreach (var a in pool) odds[a] = odds.GetValueOrDefault(a) + weights[x] / 256.0 / pool.Length;
                        }
                        swapsBefore = WcAbilityScaling.Swaps;
                        var seen = new Dictionary<int, int>();
                        int n = 400, aimed = 0;
                        for (int i = 0; i < n; i++)
                        {
                            var made = create.Invoke(act, new object[] { s.Base + 13, null, BattleActData.ActType.Normal }) as BattleActData;
                            int id = made?.abilityList != null && made.abilityList.Count > 0 ? made.abilityList[0]?.Id ?? 0 : 0;
                            seen[id] = seen.GetValueOrDefault(id) + 1;
                            var t = made?.TargetUnitDatas;
                            if (t != null && t.Length > 0 && t.All(u => u?.TryCast<BattlePlayerData>() != null)) aimed++;
                        }
                        string mix = string.Join(", ", seen.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value / (double)n:0.00} (WC {odds.GetValueOrDefault(kv.Key):0.00})"));
                        Plugin.L.LogInfo($"[wc test] ability_scaling: Sandhorse Lv {level}, base tier {baseTier}: {mix}");
                        Check(WcAbilityScaling.Swaps - swapsBefore == n, $"ability scaling: every act swapped ({WcAbilityScaling.Swaps - swapsBefore} of {n})");
                        Check(seen.Keys.All(odds.ContainsKey), $"ability scaling: only spells of WC's tiers for Lv {level} ({mix})");
                        Check(odds.All(kv => Math.Abs(seen.GetValueOrDefault(kv.Key) / (double)n - kv.Value) < 0.08), $"ability scaling: WC's odds ({mix})");
                        Check(aimed == n, $"ability scaling: every act aimed at the party ({aimed} of {n})");
                        swapsBefore = WcAbilityScaling.Swaps;
                        return true;
                    }),
                    (25, () =>
                    {
                        natural = WcAbilityScaling.Swaps - swapsBefore;
                        Check(natural > 0, $"ability scaling: the Sandhorse's own turns swapped its spell ({natural}; last: {WcAbilityScaling.LastSwap})");
                        return false;
                    }),
                };
            }
            if (name == "mug_auto")
            {
                // auto-battle with Mug as a character's first command and no Fight (the user's seed
                // gaadl68ix7j4: Gau, 2026-11-11, mugged himself): with no history the game builds
                // Fight, swaps it for Defend on the character itself (no Fight command), and
                // WcAutobattle makes the default a Mug: at an enemy, never the character.
                // Then a second battle (2026-11-12, Gau "sits there with a full ATB bar, sometimes it
                // works"): its first replay starts from the first battle's Mug, aimed at a monster of
                // that battle; the character must go on mugging this battle's monsters.
                // Request "mug=old": the earlier patch's replay (the remembered act as it was), to
                // show the stall it left
                int lead = -1;
                var saved = new int[6];
                Il2CppSystem.Collections.Generic.List<Command> savedList = null;
                CharacterStatus row = null;
                var firstFoes = new List<BattleEnemyData>();
                int split = -1, waits = 0;
                bool requeued = false;
                WcAutobattle.ReplayAsBefore = req != null && req.GetValueOrDefault("mug", "") == "old";
                BattleUnitData Me() => BattlePlugManager.Instance()?.GetPlayerUnits()?.ToArray()
                    .FirstOrDefault(u => u?.GetOwnedCharacterData()?.characterStatusId == lead);
                // The party never dies, nor does one monster (the anchor): a won fight waits on its
                // results screen (no input in a headless run); the forced end skips the results. In
                // the first battle (party 9: four monsters) the others die, so the character's
                // remembered Mug target dies under it; in the second every monster is kept
                int fight = 1, deathsAt = -1;
                IntPtr anchor = IntPtr.Zero;
                var dead = new HashSet<IntPtr>();
                bool toldList = false;
                void KeepAlive()
                {
                    var b = BattlePlugManager.Instance();
                    if (b == null || !b.IsBattle()) return;
                    var foes = b.GetEnemyUnits();
                    if (fight == 1 && anchor == IntPtr.Zero && foes != null && foes.Length > 0 && foes[foes.Length - 1] != null)
                        anchor = foes[foes.Length - 1].Pointer;
                    for (int i = 0; foes != null && i < foes.Length; i++)
                    {
                        var e = foes[i];
                        var ep = e?.BattleUnitDataInfo?.Parameter;
                        if (ep == null || fight != 1 || e.Pointer == anchor) continue;
                        if (ep.CurrentHP <= 0 && dead.Add(e.Pointer))
                        {
                            if (deathsAt < 0) deathsAt = WcBattles.Acts.Count;
                            Plugin.L.LogInfo($"[wc test] mug_auto: a monster died after {WcBattles.Acts.Count} acts");
                        }
                    }
                    // the battle reads the character's commands from the unit's own data, which the
                    // field-side change above didn't reach (the first runs: [1 26 19 ...], so the game's
                    // IsHaveMagic turned every replay down and only the default Mug ran); a seed's Mug
                    // is there, as the user's Gau's is
                    var mine = Me()?.GetOwnedCharacterData();
                    var cmds = mine?.CommandList;
                    if (cmds != null && (cmds.Count == 0 || cmds[0]?.Id != 11))
                    {
                        var list = new Il2CppSystem.Collections.Generic.List<Command>();
                        list.Add(MasterManager.Instance.GetData<Command>(11));
                        foreach (var c in cmds) if (c != null && c.Id != 1 && c.Id != 11) list.Add(c);
                        mine.CommandList = list;
                        if (!toldList)
                        {
                            toldList = true;
                            var field = UserDataManager.Instance()?.GetPartyMemberData()?.ToArray().FirstOrDefault(c => c?.characterStatusId == lead);
                            Plugin.L.LogInfo($"[wc test] mug_auto: the battle unit's commands were [{string.Join(" ", cmds.ToArray().Select(c => c?.Id ?? 0))}]; " +
                                             $"its data is the party's: {field != null && field.Pointer == mine.Pointer}");
                        }
                    }
                    var units = new List<BattleUnitData>();
                    foreach (var p in b.GetPlayerUnits()) units.Add(p);
                    foreach (var e in b.GetEnemyUnits())
                        if (e != null && (fight != 1 || e.Pointer == anchor)) units.Add(e);
                    foreach (var u in units)
                    {
                        var pr = u?.BattleUnitDataInfo?.Parameter;
                        if (pr != null && pr.CurrentHP > 0 && pr.CurrentHP < 9999)
                        {
                            if (pr.BaseMaxHp < 9999) pr.BaseMaxHp = 9999;
                            pr.CurrentHP = 9999;
                        }
                    }
                }
                void Restore()
                {
                    recorder = null;
                    WcAutobattle.ReplayAsBefore = false;
                    if (row != null)
                    {
                        row.CommandId1 = saved[0]; row.CommandId2 = saved[1]; row.CommandId3 = saved[2];
                        row.CommandId4 = saved[3]; row.CommandId5 = saved[4]; row.CommandId6 = saved[5];
                    }
                    var owned = UserDataManager.Instance()?.GetPartyMemberData()?.ToArray().FirstOrDefault(c => c?.characterStatusId == lead);
                    if (owned != null && savedList != null) owned.CommandList = savedList;
                }
                string Show(IEnumerable<(int Who, int Command, string Targets)> acts) =>
                    string.Join("; ", acts.Select(a => $"{a.Who}:{a.Command}->[{a.Targets}]"));
                var steps = new List<(float, Func<bool>)>
                {
                    (0, () =>
                    {
                        var party = UserDataManager.Instance()?.GetPartyMemberData();
                        var owned = party != null && party.Count > 0 ? party[0] : null;
                        if (owned == null) { Check(false, "mug_auto: no party"); return false; }
                        lead = owned.characterStatusId;
                        row = MasterManager.Instance.GetData<CharacterStatus>(lead);
                        saved = new[] { row.CommandId1, row.CommandId2, row.CommandId3, row.CommandId4, row.CommandId5, row.CommandId6 };
                        row.CommandId1 = 11;
                        if (row.CommandId2 == 1) row.CommandId2 = 0;
                        if (row.CommandId3 == 1) row.CommandId3 = 0;
                        if (row.CommandId4 == 1) row.CommandId4 = 0;
                        if (row.CommandId5 == 1) row.CommandId5 = 0;
                        if (row.CommandId6 == 1) row.CommandId6 = 0;
                        savedList = owned.CommandList;
                        var list = new Il2CppSystem.Collections.Generic.List<Command>();
                        list.Add(MasterManager.Instance.GetData<Command>(11));
                        if (savedList != null)
                            foreach (var c in savedList) if (c != null && c.Id != 1 && c.Id != 11) list.Add(c);
                        owned.CommandList = list;
                        WcBattles.Acts.Clear();
                        recorder = KeepAlive;
                        WcBattles.Queue(9);
                        Plugin.L.LogInfo($"[wc test] mug_auto: character {lead} has Mug first, no Fight; battle 9 queued");
                        return true;
                    }),
                    (8, () =>
                    {
                        var battle = BattlePlugManager.Instance();
                        Check(battle != null && battle.IsBattle(), "mug_auto: in battle");
                        var ui = Last.UI.BattleUIManager.Instance;
                        ui?.SetIsAuto(true);
                        Check(ui != null && ui.IsAuto(), "mug_auto: auto-battle on");
                        // a Mug aimed at the character itself, as the earlier default made it and
                        // its replays kept it: turned on an enemy
                        var me = Me();
                        if (me != null)
                        {
                            var fixedAct = WcAutobattle.ReplayMug(null, MugAt(me, me), lead);
                            var t = fixedAct?.TargetUnitDatas;
                            Check(t != null && t.Length == 1 && WcAutobattle.Muggable(t[0], battle.GetEnemyUnits()) && (int)fixedAct.CurrentTargetType == 3,
                                  $"mug_auto: a Mug aimed at itself is turned on an enemy (type {(fixedAct == null ? -1 : (int)fixedAct.CurrentTargetType)})");
                        }
                        else Check(false, "mug_auto: the character's battle unit");
                        firstFoes.Clear();
                        foreach (var e in battle?.GetEnemyUnits() ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BattleEnemyData>(0))
                            if (e != null) firstFoes.Add(e);
                        return true;
                    }),
                    (22, () =>
                    {
                        var mine = WcBattles.Acts.Where(a => a.Who == lead).ToList();
                        Plugin.L.LogInfo($"[wc test] mug_auto: battle 1 acts {Show(WcBattles.Acts)}");
                        var mugs = mine.Where(a => a.Command == 11).ToList();
                        Check(mugs.Count > 0, $"mug_auto: character {lead} mugged in auto-battle ({mine.Count} acts)");
                        Check(mugs.All(a => a.Targets.Length > 0 && a.Targets.Split(' ').All(t => t == "e")),
                              $"mug_auto: every Mug at an enemy ({string.Join(", ", mugs.Select(a => a.Targets))})");
                        // after the first monster died: the character kept its turns (a stalled one sits
                        // with a full gauge while the others go on acting)
                        if (deathsAt < 0) Check(false, "mug_auto: a monster died in the first battle");
                        else
                        {
                            var after = WcBattles.Acts.Skip(deathsAt).Where(a => a.Who > 0).ToList();
                            int last = after.FindLastIndex(a => a.Who == lead);
                            int trailing = last < 0 ? after.Count : after.Count - 1 - last;
                            Check(last >= 0 && trailing <= 4,
                                  $"mug_auto: character {lead} kept acting after monsters died ({after.Count(a => a.Who == lead)} of its acts after the first death; " +
                                  $"{trailing} by the others since its last; {dead.Count} died)");
                        }
                        split = WcBattles.Acts.Count;
                        // the judge honours isForce only when enabled and of the Normal type
                        var j = BattlePlugManager.Instance()?.BattleEndJugment;
                        if (j != null)
                        {
                            j.SetEnabled(true);
                            j.CurrentEndJugmentType = BattleEndJugment.EndJugmentType.Normal;
                            j.isForce = true;
                        }
                        return true;
                    }),
                };
                // the first battle's end (the results screen), then the second battle
                for (int w = 0; w < 8; w++)
                    steps.Add((3f, () =>
                    {
                        if (requeued) return true;
                        var b = BattlePlugManager.Instance();
                        if (b != null && b.IsBattle()) { waits++; return true; }
                        fight = 2;
                        WcBattles.Queue(7);
                        requeued = true;
                        Plugin.L.LogInfo($"[wc test] mug_auto: battle 1 over; battle 7 queued again");
                        return true;
                    }));
                steps.Add((10f, () =>
                {
                    var battle = BattlePlugManager.Instance();
                    bool on = requeued && battle != null && battle.IsBattle();
                    Check(on, $"mug_auto: a second battle ({(requeued ? "queued" : $"the first still on after {waits * 3} s")})");
                    if (!on) { Restore(); return false; }
                    Last.UI.BattleUIManager.Instance?.SetIsAuto(true);
                    // the history's Mug, aimed at a monster of the first battle: re-aimed at one of this battle
                    var me = Me();
                    var stale = firstFoes.FirstOrDefault();
                    if (me != null && stale != null)
                    {
                        var fixedAct = WcAutobattle.ReplayMug(null, MugAt(me, stale), lead);
                        var t = fixedAct?.TargetUnitDatas;
                        Check(!WcAutobattle.Muggable(stale, battle.GetEnemyUnits()) && t != null && t.Length == 1 &&
                              WcAutobattle.Muggable(t[0], battle.GetEnemyUnits()),
                              "mug_auto: a Mug aimed at the first battle's monster is turned on one of this battle");
                    }
                    else Check(false, $"mug_auto: the character's battle unit ({me != null}) and the first battle's monsters ({firstFoes.Count})");
                    return true;
                }));
                steps.Add((20f, () =>
                {
                    var after = WcBattles.Acts.Skip(Math.Max(0, split)).ToList();
                    Plugin.L.LogInfo($"[wc test] mug_auto: battle 2 acts {Show(after)}");
                    var mugs = after.Where(a => a.Who == lead && a.Command == 11).ToList();
                    Check(mugs.Count > 0 && mugs.All(a => a.Targets.Length > 0 && a.Targets.Split(' ').All(t => t == "e")),
                          $"mug_auto: character {lead} went on mugging in the second battle ({mugs.Count} Mugs: {string.Join(", ", mugs.Select(a => a.Targets))}; " +
                          $"{after.Count(a => a.Who != lead)} acts by the others)");
                    Restore();
                    return false;
                }));
                return steps;
            }
            if (name == "nxppd")
                return new List<(float, Func<bool>)> { (0f, NxppdCheck) };
            if (name == "getitem")
            {
                // the game's own GetItem op in a script, as vanilla's story pick-ups give gil (the Cave to
                // the Sealed Gate's sc_e_0453: 293 gil): the porter's scripted chests give their contents
                // that way in mods without the WC plugin (BNW)
                int had = -1;
                return new List<(float, Func<bool>)>
                {
                    (0, () => { Check(WcEvents.GotoMap(408, 1), "getitem: to the Cave to the Sealed Gate (map 408)"); return true; }),
                    (4, () =>
                    {
                        had = UserDataManager.Instance()?.OwendGil ?? -1;
                        Check(WcEvents.RunScript("sc_e_0453"), "getitem: sc_e_0453 ran");
                        return true;
                    }),
                    (5, () =>
                    {
                        int now = UserDataManager.Instance()?.OwendGil ?? -1;
                        Check(now == had + 293, $"getitem: gil {had} -> {now} (GetItem [1, 293])");
                        return false;
                    }),
                };
            }
            if (name == "kt_skip")
            {
                // -fbs (both presets): Kefka's Tower's drop past the Demon statue (WC's Poltergeist:
                // map 564, the door at (36, 25), the ledge at (36, 28)) only once the Demon is beaten
                // (ScenarioFlag2 238); before, the drop pushes the party back (tools/wc_events.py)
                var cells = new Dictionary<int, UnityEngine.Vector3?>();
                var plan = new List<(float, Func<bool>)>();
                foreach (int beaten in new[] { 0, 1 })
                {
                    int b = beaten;
                    plan.Add((1f, () =>
                    {
                        DataStorage.Instance.Set(DataStorage.Category.kScenarioFlag2, 238, b);
                        Check(WcEvents.GotoMap(564, 1), $"kt_skip: to the Demon's back door (from map 570), Demon {(b == 1 ? "beaten" : "standing")}");
                        return true;
                    }));
                    plan.Add((4f, () =>
                    {
                        // where things are at run time (cells, not the map file's tiles)
                        var fc = Field();
                        var found = new List<string>();
                        if (fc?.entityList != null)
                            foreach (var e in fc.entityList)
                            {
                                var pe = e?.Property?.TryCast<Last.Map.PropertyEvent>();
                                if (pe == null || pe.ScriptId <= 0) continue;
                                var c = fc.ConvertWorldPositionToCellPosition(e.transform.position);
                                found.Add($"{pe.ObjectType}/{pe.ActionId}:{pe.ScriptId}@({Mathf.Round(c.x)},{Mathf.Round(c.y)})");
                            }
                        Plugin.L.LogInfo($"[wc test] kt_skip: player at {WcEvents.PlayerCell()}, scripted {string.Join(" ", found)}");
                        return true;
                    }));
                    // one step onto the ledge (10, 9 at run time), then hands off: the test's stick
                    // reaches the field even while an event runs, which a player's can't
                    plan.Add((0.5f, () => { Walk(0, -1, 0.25f); return true; }));
                    plan.Add((3f, () =>
                    {
                        cells[b] = WcEvents.PlayerCell();
                        Plugin.L.LogInfo($"[wc test] kt_skip: Demon {(b == 1 ? "beaten" : "standing")}: walked south to {cells[b]} (map {UserDataManager.Instance()?.CurrentMapId})");
                        return true;
                    }));
                }
                plan.Add((0f, () =>
                {
                    bool on = WcSeeds.Active?.Flags.Split(' ').Contains("-fbs") == true;
                    var standing = cells.GetValueOrDefault(0); var beatenCell = cells.GetValueOrDefault(1);
                    Check(standing.HasValue && beatenCell.HasValue && (on ? standing.Value != beatenCell.Value : standing.Value == beatenCell.Value),
                          $"kt_skip (-fbs {(on ? "on" : "off")}): Demon standing -> {standing}, beaten -> {beatenCell} (the drop {(on ? "only once beaten" : "always open")})");
                    return false;
                }));
                return plan;
            }
            if (name == "fade_selftest")
            {
                // the black-screen check sees a black screen: faded out it says black, faded in not
                var black = new Color(0, 0, 0, 1);
                return new List<(float, Func<bool>)>
                {
                    (0, () => { Il2CppSystem.Fade.FadeManager.Instance.FadeOut(0.2f, black); return true; }),
                    (2, () => { var (b, s) = ScreenBlack(); Check(b, $"fade self-test: faded out reads black ({s})"); return true; }),
                    (0, () => { Il2CppSystem.Fade.FadeManager.Instance.FadeIn(0.2f, black); return true; }),
                    (2, () => { var (b, s) = ScreenBlack(); Check(!b, $"fade self-test: faded in reads not black ({s})"); return true; }),
                };
            }
            var seed = WcSeeds.Active;
            var (module, rn) = SubChecks.TryGetValue(name, out var sub) ? sub : (name, 0);
            if (!seed.Checks.TryGetValue(module, out var c)) { Check(false, $"the seed has no check {module}"); return null; }
            if (req.TryGetValue("reward", out var forced))   // test only: "character 9", "esper 76", "item 12"
            {
                var parts = forced.Split(' ');
                c.Rewards[0] = (parts[0], int.Parse(parts[1]));
                Plugin.L.LogInfo($"[wc test] {name}'s reward set to {forced} for this test");
            }
            int flag = CheckFlags.TryGetValue(name, out var f) ? f : -1;
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
                return new List<(float, Func<bool>)>
                {
                    (0, Warp(map, 1)), (0, closedThenRecruit), (0, GateIs(true)), (6, Run(script)), (2, Expect(true)), (5, NotBlack()),
                };
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
            step = Step.Wait;
            Mute();
            Plugin.L.LogInfo($"[wc test] requested: {string.Join(", ", req.Select(kv => kv.Key + "=" + kv.Value))}");
        }

        // silent run: the game's sound engine (SEAD) and Unity's audio at volume 0, every frame
        static void Mute()
        {
            try { SEAD.API.seadSetMasterVolume(0f, 0f); } catch { }
            try { AudioListener.volume = 0f; } catch { }
        }

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
                        var monster = File.ReadAllLines(Path.Combine(Plugin.Folder, "baseline", "vanilla_data", LiveData.MasterDir, "monster.csv"));
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
                        var slotData = mgr?.GetLatestSaveSlotData(false, false);
                        if (slotData == null) { Check(false, "no save to load"); Finish(); return; }
                        Plugin.L.LogInfo($"[wc test] loading save slot {slotData.Id} (GotoLoadSaveData, hooked)");
                        mgr.GotoLoadSaveData(slotData);
                        stepAt = now;
                        step = Step.Loading;
                        return;
                    }
                case Step.Loading:
                    if (Object.FindObjectOfType<FieldMap>() != null)
                    {
                        Check(true, $"save loaded to the field after the hook ({now - stepAt:0.0} s)");
                        // a New Game's WC start state, granted here to the loaded game (memory only)
                        var seed = made.Count > 0 ? WcSeeds.Load(made[0]) : null;
                        var udm = UserDataManager.Instance();
                        if (seed != null && udm != null)
                        {
                            int gil = udm.OwendGil;
                            var msg = WcSeeds.GrantStart(seed);
                            Check(msg != null && udm.OwendGil == Math.Min(9999999, gil + seed.Gold),
                                  $"start state granted: {msg ?? "failed"}; gil {gil} -> {udm.OwendGil} (seed {seed.Gold}), items {string.Join(" ", seed.Items.Select(i => $"{i[0]}x{i[1]}"))}");
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
                Check(true, "devices: " + (PadFix.Check() is var devs && devs.Length > 0 ? devs : "none"));
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
