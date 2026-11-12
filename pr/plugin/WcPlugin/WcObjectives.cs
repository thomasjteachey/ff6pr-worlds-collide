using System;
using System.Collections.Generic;
using System.Linq;
using Last.Data;
using Last.Data.User;
using Last.Interpreter;
using Last.Management;

namespace WorldsCollide.PR
{
    // WC's objective results (objectives/results/*): once an objective's conditions are met
    // (WcTrack.Met; conditions the port can't track never count here), its result is applied
    // once. Saved in ScenarioFlag4 (vanilla leaves 207-255 free):
    //   207 + i   objective i complete (result applied to the party as it was then)
    //   224 + c   character c (status id 1-14) has had the "All"/random stat results applied
    //   238 + i   objective i's learn result applied (its character may join later)
    // Results ported: stat +/- (All, one character), Learn SwdTechs/Blitzes/Lores/Dances, item
    // gifts (the items WC's generator wrote, wc_seed.json "items"), Full Heal; Unlock Final Kefka
    // is the stairway's gate (WcEvents.FinalKefka). The rest are logged as not ported yet.
    static class WcObjectives
    {
        const int Complete = 207, StatsApplied = 224, LearnApplied = 238;
        static float nextPoll;
        static int polls;

        static int F4(int i) => WcEvents.Flag(DataStorage.Category.kScenarioFlag4, i);
        static void SetF4(int i) => DataStorage.Instance?.Set(DataStorage.Category.kScenarioFlag4, i, 1);

        internal static bool IsComplete(int index) => index < 16 && F4(Complete + index) == 1;

        // every second in a WC game, outside events
        public static void Poll()
        {
            if (UnityEngine.Time.unscaledTime < nextPoll) return;
            nextPoll = UnityEngine.Time.unscaledTime + 1f;
            try { WcItems.Apply(); } catch (Exception e) { WcRunner.WarnOnce("weapons", e); }
            try { WcBattles.StartQueued(); } catch (Exception e) { WcRunner.WarnOnce("queued-battle", e); }
            if (polls % 2 == 0)
                try { WcEvents.WorldSync(); } catch (Exception e) { WcRunner.WarnOnce("world-sync", e); }
            WcChallenges.EnsureSketchRoll();
            try { WcAchievements.Refresh(); } catch (Exception e) { WcRunner.WarnOnce("achievements", e); }
            if (++polls % 3 == 0)
                try
                {
                    var b = Last.Battle.BattlePlugManager.Instance();
                    if (WcSeeds.Active != null && Plugin.WcMode && F4(WcEvents.WcGameFlag) == 1 && (b == null || !b.IsBattle()) && !WcEvents.EventBusy())
                    {
                        WcSkills.Sync();
                        WcRages.StartSkills();
                        WcEspers.RestoreMagicCommands();
                        if (Completed("Magitek Upgrade")) MagitekUpgrade();
                    }
                }
                catch (Exception e) { WcRunner.WarnOnce("skills-poll", e); }
            try
            {
                var s = WcSeeds.Active;
                if (s == null || s.Objectives.Count == 0 || !Plugin.WcMode || F4(WcEvents.WcGameFlag) != 1) return;
                if (UserDataManager.Instance() == null || WcEvents.EventBusy()) return;
                for (int i = 0; i < s.Objectives.Count && i < 16; i++)
                {
                    if (IsComplete(i)) continue;
                    var o = s.Objectives[i];
                    if (o.Conditions.Count(c => WcTrack.Met(c) == true) < o.Required) continue;
                    SetF4(Complete + i);
                    var how = Apply(i, o);
                    Plugin.L.LogInfo($"[wc] objective {o.Letter} complete: {o.Result} ({how})");
                    WcScreen.Toast($"Objective {o.Letter} complete: {o.Result}");
                }
            }
            catch (Exception e) { WcRunner.WarnOnce("objectives", e); }
        }

        // (vanilla learner, PR command, first ability, count): the techniques go to a holder of the
        // command (WC randomizes commands; WcSkills shares them with the other holders), else the
        // vanilla learner, else whoever of them joins first
        static readonly Dictionary<string, (int Character, int Command, int First, int Count)> Learn =
            new Dictionary<string, (int, int, int, int)>
        {
            ["Learn SwdTechs"] = (3, 12, 124, 8),   // Bushido, Fang..Oblivion (WC's swdtech order)
            ["Learn Blitzes"] = (6, 15, 132, 8),    // Raging Fist..Phantom Rush
            ["Learn Lores"] = (8, 17, 140, 24),     // Doom..Self-Destruct
            ["Learn Dances"] = (11, 22, 164, 8),    // Wind Rhapsody..Snowman Rondo
        };

        static OwnedCharacterData LearnTarget(string result) =>
            WcSkills.Holder(Learn[result].Command) ?? Owned(Learn[result].Character);

        static readonly Dictionary<string, Action<PlayerCharacterParameter, int>> Stats = new Dictionary<string, Action<PlayerCharacterParameter, int>>
        {
            ["Vigor"] = (p, n) => p.AddtionalPower += n,
            ["Speed"] = (p, n) => p.AddtionalAgility += n,
            ["Stamina"] = (p, n) => p.AddtionalVitality += n,
            ["MagPwr"] = (p, n) => p.AddtionalMagic += n,
        };

        // WC's "Auto <status>" results: the party starts every battle with that status (the PR's
        // conditions; Dog Block, Life 3, Overcast and Seizure have no plain counterpart yet)
        static readonly Dictionary<string, Last.Defaine.ConditionType> AutoStatus = new Dictionary<string, Last.Defaine.ConditionType>
        {
            ["Auto Berserk"] = Last.Defaine.ConditionType.Bersak, ["Auto Condemned"] = Last.Defaine.ConditionType.Doom,
            ["Auto Float"] = Last.Defaine.ConditionType.Leviteto, ["Auto Haste"] = Last.Defaine.ConditionType.Heist,
            ["Auto Image"] = Last.Defaine.ConditionType.Blink, ["Auto Muddle"] = Last.Defaine.ConditionType.Confusion,
            ["Auto Mute"] = Last.Defaine.ConditionType.Silence, ["Auto Reflect"] = Last.Defaine.ConditionType.Reflex,
            ["Auto Regen"] = Last.Defaine.ConditionType.Regeneration, ["Auto Safe"] = Last.Defaine.ConditionType.Protectra,
            ["Auto Shell"] = Last.Defaine.ConditionType.Shell, ["Auto Sleep"] = Last.Defaine.ConditionType.Sleep,
            ["Auto Slow"] = Last.Defaine.ConditionType.Slow, ["Auto Dark"] = Last.Defaine.ConditionType.Blind,
            ["Auto Clear"] = Last.Defaine.ConditionType.Transparent, ["Auto Imp"] = Last.Defaine.ConditionType.Kappa,
            // Life 3 = the PR's Reraise, Seizure = Slip (HP drain), Overcast = DoomZonbie (zombie on
            // death), Dog Block = Interceptor's guard (condition type 618, condition 52)
            ["Auto Life 3"] = Last.Defaine.ConditionType.Reraise, ["Auto Seizure"] = Last.Defaine.ConditionType.Slip,
            ["Auto Overcast"] = Last.Defaine.ConditionType.DoomZonbie, ["Auto Dog Block"] = (Last.Defaine.ConditionType)618,
        };

        static bool Completed(string result)
        {
            var s = WcSeeds.Active;
            if (s == null) return false;
            for (int i = 0; i < s.Objectives.Count && i < 16; i++)
                if (s.Objectives[i].ResultName == result && IsComplete(i)) return true;
            return false;
        }

        // WC's "Magitek Upgrade" (battle/magitek_upgrade.py): everyone's Magitek menu is Terra's.
        // The PR keeps the armor's moves as owned abilities (Fire/Bolt/Ice Beam 218-220, Heal Force
        // 222 for everyone; Bio Blast 221, Confuser 223, X-Fer 224, Tek Missile 225 Terra's): anyone
        // with the armor's moves gets Terra's four too
        internal static readonly int[] MagitekBasic = { 218, 219, 220, 222 }, MagitekTerra = { 221, 223, 224, 225 };

        internal static int MagitekUpgrade()
        {
            int n = 0;
            foreach (var c in AllOwned())
            {
                if (!MagitekBasic.Any(id => WcSkills.Knows(c, id))) continue;
                foreach (var id in MagitekTerra)
                    if (!WcSkills.Knows(c, id) && WcSkills.Teach(c, id)) n++;
            }
            if (n > 0) Plugin.L.LogInfo($"[wc] Magitek Upgrade: {n} moves taught");
            return n;
        }

        // WC's "Learn Rages": the next N rages of the seed's random order the party doesn't know, to
        // a holder of Rage (command 23; WcSkills shares them), else Gau
        static string TeachRages(int i, WcSeeds.Objective o)
        {
            var c = WcSkills.Holder(23) ?? Owned(12);
            if (c == null) return "learned when a holder of Rage (or Gau) joins";
            int count = o.IntArgs.Length > 0 ? o.IntArgs[0] : 0, n = 0;
            foreach (var id in WcSeeds.Active.RageTable)
            {
                if (n >= count) break;
                if (WcSkills.Knows(c, id)) continue;
                if (WcSkills.Teach(c, id)) n++;
            }
            SetF4(LearnApplied + i);
            WcSkills.Sync();
            return $"character {c.characterStatusId} learned {n} rages";
        }

        // a battle starts (WcScaling sees it once): the completed objectives' auto-statuses on the party
        internal static string LastAuto = "";   // tests

        internal static string BattleStart(Last.Battle.BattlePlugManager battle)
        {
            var s = WcSeeds.Active;
            if (s == null || battle == null) return "";
            var types = new List<Last.Defaine.ConditionType>();
            for (int i = 0; i < s.Objectives.Count && i < 16; i++)
                if (IsComplete(i) && AutoStatus.TryGetValue(s.Objectives[i].ResultName, out var t)) types.Add(t);
            if (types.Count == 0) return "";
            var conditions = battle.BattleConditionController;
            int n = 0;
            foreach (var unit in battle.GetPlayerUnits())
            {
                if (unit == null) continue;
                foreach (var t in types)
                    try { conditions?.InterruptAddCondition(unit, t); n++; }
                    catch (Exception e) { WcRunner.WarnOnce("auto-status", e); }
            }
            var what = $"auto-statuses {string.Join(", ", types)} on the party ({n})";
            LastAuto = what;
            Plugin.L.LogInfo("[wc] " + what);
            return what;
        }

        // "Add Enemy / Dragon / Final Levels": the levels completed objectives add (WcScaling)
        internal static int LevelBonus(string resultName)
        {
            var s = WcSeeds.Active;
            if (s == null) return 0;
            int sum = 0;
            for (int i = 0; i < s.Objectives.Count && i < 16; i++)
                if (s.Objectives[i].ResultName == resultName && IsComplete(i) && s.Objectives[i].IntArgs.Length > 0)
                    sum += s.Objectives[i].IntArgs[0];
            return sum;
        }

        static string Apply(int i, WcSeeds.Objective o)
        {
            var name = o.ResultName;
            if (name == "Unlock Final Kefka") return "the stairway to Kefka opens";
            if (name == "Unlock KT Skip") return "offered after picking the tower's parties";
            if (AutoStatus.ContainsKey(name)) return "from the next battle on";
            if (name == "Add Enemy Levels" || name == "Add Dragon Levels" || name == "Add Final Levels" || name == "Add Boss Levels")
                return $"{name.Substring(4).ToLowerInvariant()} +{(o.IntArgs.Length > 0 ? o.IntArgs[0] : 0)} from the next battle";
            if (name == "Learn Spells")
            {
                var done = new List<string>();
                foreach (var c in AllOwned())
                {
                    if (F4(StatsApplied + c.characterStatusId) != 1) { BaseStats(c); continue; }
                    done.Add($"{c.characterStatusId}:{TeachSpells(c, o.IntArgs.Length > 0 ? o.IntArgs[0] : 0)}");
                }
                return "spells " + string.Join(" ", done);
            }
            if (name == "Forget Spells")
            {
                // every character but Gogo/Umaro forgets the next N known spells of the seed's order
                var done = new List<string>();
                foreach (var c in AllOwned())
                {
                    if (c.characterStatusId >= 13) continue;
                    int n = 0, count = o.IntArgs.Length > 0 ? o.IntArgs[0] : 0;
                    foreach (var id in WcSeeds.Active.ForgetTable)
                    {
                        if (n >= count) break;
                        foreach (var a in c.OwnedAbilityList)
                            if (a?.Ability != null && a.Ability.Id == id && a.SkillLevel >= 100) { a.SkillLevel = 0; n++; break; }
                    }
                    done.Add($"{c.characterStatusId}:{n}");
                }
                return "forgot " + string.Join(" ", done);
            }
            if (name == "Imp Song" || name == "Sour Mouth")
            {
                // field statuses on the party: Imp (condition 13); Sour Mouth adds Blind (9) and Poison (10)
                var ids = name == "Imp Song" ? new[] { 13 } : new[] { 9, 10, 13 };
                var party = UserDataManager.Instance()?.GetPartyMemberData();
                int n = 0;
                if (party != null)
                    foreach (var c in party)
                    {
                        if (c == null) continue;
                        foreach (var id in ids)
                        {
                            bool has = false;
                            var list = c.Parameter?.CurrentConditionList;
                            if (list != null) foreach (var x in list) if (x?.Id == id) has = true;
                            if (has) continue;
                            new OwnedCharacterClient().AddCondition(c.Id, id);   // the game's own (the list is a copy)
                            n++;
                        }
                    }
                return $"party statuses {string.Join("/", ids)} ({n})";
            }
            if (name == "Learn Rages") return TeachRages(i, o);
            if (name == "Magitek Upgrade") return $"Magitek moves {MagitekUpgrade()} taught (more as the armor's riders join)";
            if (name == "Max Morph Duration")
            {
                // SNES $1CF6 (the party's morph supply) = 255; the PR keeps it per character
                // (OwnedCharacterData.MagicLearningValue: the gauge = it x 0.5 or x 1)
                int n = 0;
                foreach (var c in AllOwned()) { if (c.MagicLearningValue < 255) { c.MagicLearningValue = 255; n++; } }
                return $"morph supply full ({n})";
            }
            if (name == "Fallen One")
            {
                foreach (var c in AllOwned()) c.Parameter.CurrentHP = 1;
                return "party at 1 HP";
            }
            if (Learn.ContainsKey(name))
            {
                if (LearnTarget(name) == null) return $"learned when a holder of command {Learn[name].Command} (or character {Learn[name].Character}) joins";
                var r = TeachNext(i, o);
                WcSkills.Sync();
                return r;
            }
            var stat = name.Split(' ')[0];
            if (Stats.ContainsKey(stat) && (name.EndsWith(" All") || name.EndsWith(" Random")))
            {
                var done = new List<string>();
                foreach (var c in AllOwned())
                {
                    if (F4(StatsApplied + c.characterStatusId) != 1) { BaseStats(c); continue; }   // the full sum, this one included
                    int n = StatFor(o, c.characterStatusId);
                    if (n == 0) continue;
                    Stats[stat](c.Parameter, n);
                    done.Add($"{c.characterStatusId}:{n:+0;-0}");
                }
                return "stats " + string.Join(" ", done);
            }
            if (o.Items.Count > 0)
            {
                var client = new OwnedItemClient();
                foreach (var it in o.Items)
                    if (it[0] > 0) client.AddOwnedItem(it[0], Math.Max(1, it[1]));
                return "items " + string.Join(", ", o.Items.Select(x => $"{x[0]}x{x[1]}"));
            }
            if (name == "Full Heal")
            {
                foreach (var c in AllOwned())
                {
                    var p = c.Parameter;
                    p.CurrentHP = p.ConfirmedMaxHp();
                    p.CurrentMP = p.ConfirmedMaxMp();
                }
                return "party healed";
            }
            return "not in the Pixel Remaster port yet";
        }

        // what objective o's stat result gives character c (status id): All = everyone, Random = one
        static int StatFor(WcSeeds.Objective o, int statusId)
        {
            if (o.IntArgs.Length == 0) return 0;
            if (o.ResultName.EndsWith(" All")) return o.IntArgs[0];
            return o.IntArgs.Length >= 3 && o.IntArgs[2] + 1 == statusId ? o.IntArgs[0] : 0;
        }

        // a character's first time: every completed stat result at once
        static void BaseStats(OwnedCharacterData c)
        {
            var s = WcSeeds.Active;
            int id = c.characterStatusId;
            if (s == null || id < 1 || id > 14 || F4(StatsApplied + id) == 1) return;
            SetF4(StatsApplied + id);
            for (int i = 0; i < s.Objectives.Count && i < 16; i++)
            {
                if (!IsComplete(i)) continue;
                var o = s.Objectives[i];
                if (o.ResultName == "Learn Spells") { TeachSpells(c, o.IntArgs.Length > 0 ? o.IntArgs[0] : 0); continue; }
                var stat = o.ResultName.Split(' ')[0];
                if (!Stats.ContainsKey(stat)) continue;
                int n = StatFor(o, id);
                if (n != 0) Stats[stat](c.Parameter, n);
            }
        }

        // a character joins (WcEvents.Join_Postfix): their completed stat and learn results
        internal static void Joined(int statusId)
        {
            try
            {
                var s = WcSeeds.Active;
                var c = Owned(statusId);
                if (s == null || c == null) return;
                BaseStats(c);
                for (int i = 0; i < s.Objectives.Count && i < 16; i++)
                {
                    var o = s.Objectives[i];
                    if (IsComplete(i) && Learn.ContainsKey(o.ResultName) && F4(LearnApplied + i) != 1 && LearnTarget(o.ResultName) != null)
                        Plugin.L.LogInfo($"[wc] objective {o.Letter} on joining: {TeachNext(i, o)}");
                    if (IsComplete(i) && o.ResultName == "Learn Rages" && F4(LearnApplied + i) != 1 && (WcSkills.Holder(23) ?? Owned(12)) != null)
                        Plugin.L.LogInfo($"[wc] objective {o.Letter} on joining: {TeachRages(i, o)}");
                    if (IsComplete(i) && o.ResultName == "Max Morph Duration" && c.MagicLearningValue < 255)
                        c.MagicLearningValue = 255;
                }
                WcRages.StartSkills();
                WcSkills.Sync();
            }
            catch (Exception e) { WcRunner.WarnOnce("objectives-join", e); }
        }

        // WC's "Learn Spells": the character learns the next `count` spells of the seed's table not known yet
        static int TeachSpells(OwnedCharacterData c, int count)
        {
            var table = WcSeeds.Active?.SpellTable;
            if (table == null || count <= 0) return 0;
            int taught = 0;
            foreach (var id in table)
            {
                if (taught >= count) break;
                OwnedAbility owned = null;
                foreach (var a in c.OwnedAbilityList)
                    if (a?.Ability != null && a.Ability.Id == id) owned = a;
                if (owned != null && owned.SkillLevel >= 100) continue;
                owned ??= new OwnedCharacterClient().AddAbility(c.Id, id);
                if (owned == null) continue;
                owned.SkillLevel = 100;
                taught++;
            }
            return taught;
        }

        // WC's learn results: the next `count` of the character's techniques not known yet
        static string TeachNext(int i, WcSeeds.Objective o)
        {
            var (_, _, first, total) = Learn[o.ResultName];
            var c = LearnTarget(o.ResultName);
            if (c == null) return "character not here";
            int who = c.characterStatusId;
            int count = o.IntArgs.Length > 0 ? o.IntArgs[0] : total;
            var taught = new List<int>();
            for (int id = first; id < first + total && taught.Count < count; id++)
            {
                OwnedAbility owned = null;
                foreach (var a in c.OwnedAbilityList)
                    if (a?.Ability != null && a.Ability.Id == id) owned = a;
                if (owned != null && owned.SkillLevel >= 100) continue;
                owned ??= new OwnedCharacterClient().AddAbility(c.Id, id);
                if (owned == null) continue;
                owned.SkillLevel = 100;
                taught.Add(id);
            }
            SetF4(LearnApplied + i);
            return $"character {who} learned {string.Join(",", taught)}";
        }

        static IEnumerable<OwnedCharacterData> AllOwned()
        {
            var list = UserDataManager.Instance()?.OwnedCharacterList;
            if (list == null) yield break;
            foreach (var c in list)
                if (c != null && c.characterStatusId >= 1 && c.characterStatusId <= 14) yield return c;
        }

        static OwnedCharacterData Owned(int statusId) => AllOwned().FirstOrDefault(c => c.characterStatusId == statusId);

        // tests: what the results did, by objective
        internal static string TestApply(int i) => Apply(i, WcSeeds.Active.Objectives[i]);
    }
}
